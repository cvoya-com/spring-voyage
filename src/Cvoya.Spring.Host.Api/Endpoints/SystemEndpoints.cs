// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Text.Json.Serialization;

using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

// Note: this endpoint group previously hosted `POST /api/v1/system/credentials/{provider}/validate`,
// which delegated to IProviderCredentialValidator. Phase 3.16 (#690) retired
// that path in favour of the per-runtime `POST /api/v1/agent-runtimes/{id}/validate-credential`
// route. The status probe (GET /status) remains because the agent/unit
// Execution panels depend on the tenant-default resolvability signal it
// surfaces.

/// <summary>
/// Platform-system-status endpoints. Today this group exposes a
/// read-only credential-status probe used by the unit-creation wizard
/// (#598) to tell operators whether the selected LLM provider's
/// credentials are actually configured — so they're not surprised at
/// dispatch time by a "not configured" failure when they're 4 wizard
/// steps deep.
/// </summary>
/// <remarks>
/// <para>
/// <b>Key material never crosses this boundary.</b> The response body is
/// intentionally limited to booleans + source enum + a canonical secret
/// name (same string the tenant-defaults panel already shows). The
/// resolver returns plaintext on the in-process seam, but this endpoint
/// drops the value on the floor; the portal only needs "yes/no".
/// </para>
/// <para>
/// <b>Scope at request time.</b> The resolver is asked at tenant scope
/// (no unit in context) because the wizard calls this before the unit
/// exists. The OSS build has a single tenant ("local") so this is
/// equivalent to "is there a tenant-default secret"; the cloud host can
/// swap <see cref="Cvoya.Spring.Core.Tenancy.ITenantContext"/> in DI
/// without changing the endpoint.
/// </para>
/// </remarks>
public static class SystemEndpoints
{
    private const string ProviderAnthropic = "anthropic";
    private const string ProviderOpenAi = "openai";
    private const string ProviderGoogle = "google";
    private const string ProviderOllama = "ollama";

    // Machine-readable values for ProviderCredentialStatusResponse.Reason.
    // The portal switches on these to render the right banner copy; keep
    // them kebab-cased and stable — adding a new reason is additive.
    private const string ReasonNotConfigured = "not-configured";
    private const string ReasonUnreadable = "unreadable";
    private const string ReasonUnreachable = "unreachable";
    // Reported when the stored credential is present and decrypts
    // cleanly, but its shape is known-incompatible with the dispatch
    // path that will consume it (e.g. a Claude.ai OAuth token routed
    // through the Anthropic Platform REST endpoint — see #1003).
    private const string ReasonFormatRejected = "format-rejected";

    // Accepted query-parameter values for `?dispatchPath=…` — mirrors
    // Cvoya.Spring.Core.AgentRuntimes.CredentialDispatchPath. Kept as
    // strings at the wire because enum JSON binding for minimal APIs
    // is still case-sensitive and awkward; a hand-rolled switch is
    // both smaller and gives us a clear 400 surface for bad values.
    private const string DispatchPathRest = "rest";
    private const string DispatchPathAgentRuntime = "agent-runtime";

    /// <summary>
    /// Registers the system-level endpoints on <paramref name="app"/>.
    /// </summary>
    public static RouteGroupBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/platform")
            .WithTags("System");

        group.MapGet("/credentials/{provider}/status", GetCredentialStatusAsync)
            .WithName("GetProviderCredentialStatus")
            .WithSummary("Report whether an LLM provider's credentials / endpoint are configured and usable on the named dispatch path")
            .Produces<ProviderCredentialStatusResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);

        return group;
    }

    private static async Task<IResult> GetCredentialStatusAsync(
        string provider,
        ILlmCredentialResolver credentialResolver,
        IAgentRuntimeRegistry agentRuntimeRegistry,
        IHttpClientFactory httpClientFactory,
        IOptions<OllamaOptions> ollamaOptions,
        [FromQuery] string? dispatchPath,
        CancellationToken cancellationToken)
    {
        var normalized = (provider ?? string.Empty).Trim().ToLowerInvariant();

        // Conservative default: when the caller does not name a dispatch
        // path we evaluate against the strictest one (REST). Callers that
        // only ever run the in-container path can opt into the more
        // lenient evaluation explicitly.
        if (!TryParseDispatchPath(dispatchPath, out var path))
        {
            return Results.BadRequest(new
            {
                error = "unknown-dispatch-path",
                message = $"dispatchPath must be '{DispatchPathRest}' or '{DispatchPathAgentRuntime}' when supplied.",
            });
        }

        switch (normalized)
        {
            case ProviderAnthropic:
            case ProviderOpenAi:
            case ProviderGoogle:
                {
                    // Translate the endpoint's external provider token to
                    // the runtime id the registry-backed resolver expects.
                    // The portal's URL still uses the provider spelling
                    // (`anthropic`) that operators recognise, but the
                    // resolver looks runtimes up by id (`claude`).
                    var runtimeId = MapProviderToRuntimeId(normalized);

                    // No unit context — the wizard runs before the unit
                    // exists. Resolver will fall through to the tenant-
                    // scope secret, which is what the wizard cares about.
                    var resolution = await credentialResolver.ResolveAsync(
                        runtimeId,
                        unitName: null,
                        cancellationToken);

                    var resolvable = resolution.Value is { Length: > 0 };
                    var source = resolvable
                        ? MapSource(resolution.Source)
                        : null;
                    var reason = resolvable ? null : MapReason(resolution.Source);
                    var suggestion = resolvable
                        ? null
                        : BuildCredentialSuggestion(normalized, resolution.SecretName, resolution.Source);

                    // Pre-flight format check against the dispatch path the
                    // caller named. When the stored value has a known-bad
                    // shape for that path we downgrade Resolvable to false
                    // and surface `format-rejected` so the wizard does not
                    // show a green badge for a credential that will fail
                    // dispatch on the first message. (#1003)
                    if (resolvable
                        && agentRuntimeRegistry.Get(runtimeId) is { } runtime
                        && !runtime.IsCredentialFormatAccepted(resolution.Value!, path))
                    {
                        resolvable = false;
                        source = null;
                        reason = ReasonFormatRejected;
                        suggestion = BuildFormatRejectedSuggestion(normalized, path);
                    }

                    // NEVER include `resolution.Value` in the response —
                    // the endpoint is read-by-anyone (within the tenant)
                    // and the key material must stay server-side.
                    return Results.Ok(new ProviderCredentialStatusResponse(
                        Provider: normalized,
                        Resolvable: resolvable,
                        Source: source,
                        Suggestion: suggestion,
                        Reason: reason));
                }
            case ProviderOllama:
                {
                    var baseUrl = ollamaOptions.Value.BaseUrl.TrimEnd('/');
                    var (reachable, probeReason) = await ProbeOllamaAsync(
                        httpClientFactory,
                        baseUrl,
                        ollamaOptions.Value.HealthCheckTimeoutSeconds,
                        cancellationToken);

                    var suggestion = reachable
                        ? null
                        : $"Ollama not reachable at {baseUrl}. Check that the Ollama server is running. ({probeReason})";

                    return Results.Ok(new ProviderCredentialStatusResponse(
                        Provider: normalized,
                        Resolvable: reachable,
                        // Ollama has no tenant/unit secret — the reachability
                        // of the configured endpoint is deployment config
                        // (tier-1), so Source is always null.
                        Source: null,
                        Suggestion: suggestion,
                        Reason: reachable ? null : ReasonUnreachable));
                }
            default:
                return Results.BadRequest(new
                {
                    error = "unknown-provider",
                    message = "Provider must be one of: anthropic, openai, google, ollama.",
                });
        }
    }

    private static string? MapSource(LlmCredentialSource source) => source switch
    {
        LlmCredentialSource.Unit => "unit",
        LlmCredentialSource.Tenant => "tenant",
        _ => null,
    };

    private static string MapReason(LlmCredentialSource source) => source switch
    {
        LlmCredentialSource.Unreadable => ReasonUnreadable,
        _ => ReasonNotConfigured,
    };

    private static string MapProviderToRuntimeId(string provider) => provider switch
    {
        // The `anthropic` token in the endpoint's URL maps to the Claude
        // runtime id (the runtime is the plugin; `anthropic` is the
        // credential-issuing authority). Other supported providers use
        // matching spellings.
        ProviderAnthropic => "claude",
        _ => provider,
    };

    private static string BuildCredentialSuggestion(string provider, string secretName, LlmCredentialSource source)
    {
        // Mirror the canonical suggestion phrasing from docs/guide/secrets.md
        // so the portal banner and the CLI's "not configured" error read
        // identically. Portal deep-link to the Settings drawer is composed
        // on the client side.
        var displayName = provider switch
        {
            ProviderAnthropic => "Anthropic",
            ProviderOpenAi => "OpenAI",
            ProviderGoogle => "Google",
            _ => provider,
        };

        if (source == LlmCredentialSource.Unreadable)
        {
            // Slot exists but ciphertext didn't authenticate. This almost
            // always means the at-rest AES key rotated between the write
            // and the read — point the operator at the rotation playbook
            // rather than at "create the secret", which won't help.
            return $"{displayName} credentials are stored but the platform cannot decrypt the current value. " +
                $"This typically means the at-rest encryption key rotated. " +
                $"Re-save the tenant-default secret '{secretName}' from Settings → Tenant defaults, " +
                $"or restore the previous AES key.";
        }

        return $"{displayName} credentials are not configured. " +
            $"Set the tenant-default secret '{secretName}' from Settings → Tenant defaults, " +
            $"or create a unit-scoped override of the same name.";
    }

    private static string BuildFormatRejectedSuggestion(string provider, CredentialDispatchPath path)
    {
        // Only Anthropic exercises this today (Claude.ai OAuth tokens on
        // the REST path), but keep the copy generic so other runtimes
        // inheriting this signal later don't need a second branch.
        //
        // #931: The message must be operator-actionable and must not expose
        // internal implementation details (e.g. C# method names). Tell the
        // operator exactly what they need and how to remediate, referencing
        // the dispatch path that will consume the credential. The wizard
        // renders this string as-is; follow-up #931 can enrich it with the
        // chosen agent image once the wizard passes image context here.
        if (provider == ProviderAnthropic && path == CredentialDispatchPath.Rest)
        {
            return "The stored credential is a Claude.ai OAuth token (sk-ant-oat…), which the " +
                "Anthropic Platform REST API rejects. OAuth tokens require the `claude` CLI " +
                "installed inside the agent image and only work with the Claude Code in-container path. " +
                "To fix: either replace the 'anthropic-api-key' secret with an Anthropic Platform API " +
                "key (sk-ant-api…) from console.anthropic.com, or pick an agent image that includes the " +
                "`claude` CLI and select the Claude Code runtime.";
        }

        var displayName = provider switch
        {
            ProviderAnthropic => "Anthropic",
            ProviderOpenAi => "OpenAI",
            ProviderGoogle => "Google",
            _ => provider,
        };
        var pathLabel = path == CredentialDispatchPath.Rest
            ? "the host-side REST path"
            : "the in-container agent-runtime path";
        return $"The stored {displayName} credential's format is not accepted by {pathLabel}. " +
            "Replace it with a credential in the expected format for this path, " +
            "or switch to a dispatch path that accepts the current format.";
    }

    private static bool TryParseDispatchPath(string? raw, out CredentialDispatchPath path)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            // Conservative default: the strictest path. A legacy caller
            // that does not pass the param still gets the right answer
            // for the wizard's current "will this work?" question.
            path = CredentialDispatchPath.Rest;
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case DispatchPathRest:
                path = CredentialDispatchPath.Rest;
                return true;
            case DispatchPathAgentRuntime:
                path = CredentialDispatchPath.AgentRuntime;
                return true;
            default:
                path = CredentialDispatchPath.Rest;
                return false;
        }
    }

    private static async Task<(bool Reachable, string Reason)> ProbeOllamaAsync(
        IHttpClientFactory factory,
        string baseUrl,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        // Fresh probe of `/api/tags`. Mirrors OllamaHealthCheck + the
        // ListModels endpoint but with a short timeout so an unreachable
        // server doesn't stall the wizard. A richer cache-aware shape
        // (reuse the last health-probe result) is overkill for a single
        // button click; the response itself rides TanStack Query's
        // 30-second stale time on the portal side.
        try
        {
            using var client = factory.CreateClient("OllamaDiscovery");
            client.BaseAddress = new Uri(baseUrl);
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds));

            using var response = await client.GetAsync("/api/tags", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return (true, "ok");
            }
            return (false, $"HTTP {(int)response.StatusCode} {response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return (false, ex.Message);
        }
    }
}

/// <summary>
/// Response body for <c>GET /api/v1/system/credentials/{provider}/status</c>.
/// </summary>
/// <param name="Provider">Echoes the requested provider id.</param>
/// <param name="Resolvable">
/// <c>true</c> when the platform can obtain the credential <b>and</b>
/// its format is accepted by the dispatch path selected via the
/// <c>?dispatchPath</c> query parameter (for
/// Anthropic/OpenAI/Google: a non-empty secret exists at unit or
/// tenant scope, its ciphertext authenticates, and the runtime's
/// pre-flight format check clears). For Ollama: <c>true</c> when the
/// configured base URL responded to a health probe.
/// </param>
/// <param name="Source">
/// Which tier produced the credential — <c>"unit"</c> or <c>"tenant"</c>
/// — when <see cref="Resolvable"/> is <c>true</c>; <c>null</c> otherwise
/// (including for Ollama, which has no secret concept).
/// </param>
/// <param name="Suggestion">
/// Operator-facing hint to surface in the "not configured" UI state.
/// <c>null</c> when the credential is already resolvable. NEVER
/// contains the credential value itself.
/// </param>
/// <param name="Reason">
/// Machine-readable reason code when <see cref="Resolvable"/> is
/// <c>false</c>. Stable values: <c>"not-configured"</c> (no slot
/// exists), <c>"unreadable"</c> (slot exists but ciphertext did not
/// decrypt — typically an at-rest key rotation), <c>"unreachable"</c>
/// (Ollama health probe failed), and <c>"format-rejected"</c> (the
/// stored value decrypts but its shape is known-incompatible with the
/// dispatch path that will consume it — for example a Claude.ai OAuth
/// token resolved for the Anthropic REST path). <c>null</c> when
/// resolvable. The portal uses this to pick a specific banner copy;
/// additional codes may be appended in later waves.
/// </param>
public record ProviderCredentialStatusResponse(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("resolvable")] bool Resolvable,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("suggestion")] string? Suggestion,
    [property: JsonPropertyName("reason")] string? Reason = null);