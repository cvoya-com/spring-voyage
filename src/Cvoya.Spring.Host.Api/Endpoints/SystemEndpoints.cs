// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.ModelProviders;

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
    // Cvoya.Spring.Core.ModelProviders.CredentialDispatchPath. Kept as
    // strings at the wire because enum JSON binding for minimal APIs
    // is still case-sensitive and awkward; a hand-rolled switch is
    // both smaller and gives us a clear 400 surface for bad values.
    private const string DispatchPathRest = "rest";
    private const string DispatchPathAgentRuntime = "agent-runtime";
    private const string AuthMethodApiKey = "api-key";
    private const string AuthMethodOauth = "oauth";

    // Machine-readable values for ProviderCredentialStatusResponse.Paths.Summary.
    // Captures the per-path resolvability matrix surfaced by #1690 so
    // callers can reason about which dispatch paths will accept the
    // stored credential without firing a second probe with a different
    // dispatchPath query argument. The strings are stable; new entries
    // are additive.
    //
    // #1714 step 8: removed `all-paths` because the strict per-path
    // matrix means no Anthropic credential is accepted on both paths
    // simultaneously. The Claude agent-runtime path is OAuth-only; the
    // Spring Voyage REST path is API-key-only. Each shape is accepted
    // on exactly one path, so the summary is always either
    // `path-specific` (one path accepts, the other does not) or
    // `format-rejected` (no path accepts).
    //
    // - path-specific  — exactly one dispatch path accepts the stored
    //                    credential; the per-path entries below name
    //                    which one. Replaces the old `all-paths` and
    //                    `in-container-cli-only` summaries.
    private const string PathSourcePathSpecific = "path-specific";

    /// <summary>
    /// Registers the system-level endpoints on <paramref name="app"/>.
    /// </summary>
    public static RouteGroupBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/platform")
            .WithTags("System");

        group.MapGet("/credentials/{provider}/status", GetCredentialStatusAsync)
            .WithName("GetProviderCredentialStatus")
            .WithSummary("Report whether an LLM provider credential / endpoint is configured for the requested auth method")
            .Produces<ProviderCredentialStatusResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);

        return group;
    }

    private static async Task<IResult> GetCredentialStatusAsync(
        string provider,
        ILlmCredentialResolver credentialResolver,
        IRuntimeCatalog catalog,
        IModelProviderAdapterRegistry adapterRegistry,
        IHttpClientFactory httpClientFactory,
        IOptions<OllamaOptions> ollamaOptions,
        [FromQuery] string? dispatchPath,
        [FromQuery] string? authMethod,
        [FromQuery] string? agentImage,
        CancellationToken cancellationToken)
    {
        var normalized = (provider ?? string.Empty).Trim().ToLowerInvariant();

        // Conservative legacy default: when neither authMethod nor a
        // dispatch path is supplied, the old caller contract maps to the
        // REST/API-key edge. Runtime-aware callers should pass authMethod
        // directly; dispatchPath remains for path-matrix compatibility.
        if (!TryParseDispatchPath(dispatchPath, out var path))
        {
            return Results.BadRequest(new
            {
                error = "unknown-dispatch-path",
                message = $"dispatchPath must be '{DispatchPathRest}' or '{DispatchPathAgentRuntime}' when supplied.",
            });
        }
        if (!TryParseAuthMethod(authMethod, out var requestedAuthMethod))
        {
            return Results.BadRequest(new
            {
                error = "unknown-auth-method",
                message = $"authMethod must be '{AuthMethodApiKey}' or '{AuthMethodOauth}' when supplied.",
            });
        }

        switch (normalized)
        {
            case ProviderAnthropic:
            case ProviderOpenAi:
            case ProviderGoogle:
                {
                    // ADR-0038: format-acceptance lives on
                    // IModelProviderAdapter, keyed on the provider's
                    // adapter id from the catalogue.
                    var providerEntry = catalog.GetModelProvider(normalized);
                    var adapter = providerEntry is not null
                        ? adapterRegistry.Get(providerEntry.Adapter)
                        : null;
                    var probeMethod = requestedAuthMethod
                        ?? (path == CredentialDispatchPath.AgentRuntime
                            ? AuthMethod.Oauth
                            : AuthMethod.ApiKey);

                    if (providerEntry is not null
                        && !providerEntry.AuthMethods.Contains(probeMethod))
                    {
                        return Results.BadRequest(new
                        {
                            error = "unsupported-auth-method",
                            message = $"Provider '{normalized}' does not support authMethod '{FormatAuthMethod(probeMethod)}'.",
                        });
                    }

                    // ADR-0038: the resolver is keyed on (provider, authMethod).
                    var resolution = await credentialResolver.ResolveAsync(
                        normalized,
                        probeMethod,
                        agentId: null,
                        unitId: null,
                        cancellationToken);

                    var resolvable = resolution.Value is { Length: > 0 };
                    var source = resolvable
                        ? MapSource(resolution.Source)
                        : null;
                    var reason = resolvable ? null : MapReason(resolution.Source);
                    var suggestion = resolvable
                        ? null
                        : BuildCredentialSuggestion(normalized, resolution.SecretName, resolution.Source);

                    // Pre-flight format check against the requested auth
                    // method. Adapters apply method-specific format rules
                    // (e.g. Anthropic OAuth tokens vs API keys). Legacy
                    // callers without authMethod still map through
                    // dispatchPath above.
                    if (resolvable
                        && providerEntry is not null
                        && adapter is not null)
                    {
                        if (!adapter.IsCredentialFormatAccepted(providerEntry, resolution.Value!, probeMethod))
                        {
                            resolvable = false;
                            source = null;
                            reason = ReasonFormatRejected;
                            suggestion = BuildFormatRejectedSuggestion(normalized, path, agentImage);
                        }
                    }

                    // Per-path resolvability matrix: evaluate each dispatch
                    // path against the adapter so the portal / CLI can render
                    // a per-path table.
                    var paths = resolution.Value is { Length: > 0 } storedValue
                        && providerEntry is not null
                        && adapter is not null
                        ? BuildPathResolvability(adapter, providerEntry, storedValue)
                        : null;

                    return Results.Ok(new ProviderCredentialStatusResponse(
                        Provider: normalized,
                        Resolvable: resolvable,
                        Source: source,
                        Suggestion: suggestion,
                        Reason: reason,
                        Paths: paths));
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
                        Reason: reachable ? null : ReasonUnreachable,
                        // Ollama is reached over a single host-side HTTP
                        // path, so per-path matrix carries no extra
                        // signal. Skip it.
                        Paths: null));
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
        LlmCredentialSource.Agent => "agent",
        LlmCredentialSource.Unit => "unit",
        LlmCredentialSource.ParentUnit => "parent-unit",
        LlmCredentialSource.Tenant => "tenant",
        _ => null,
    };

    private static string MapReason(LlmCredentialSource source) => source switch
    {
        LlmCredentialSource.Unreadable => ReasonUnreadable,
        _ => ReasonNotConfigured,
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

    private static string BuildFormatRejectedSuggestion(
        string provider,
        CredentialDispatchPath path,
        string? agentImage = null)
    {
        // Only Anthropic exercises this today (Claude.ai OAuth tokens on
        // the REST path), but keep the copy generic so other runtimes
        // inheriting this signal later don't need a second branch.
        //
        // #931 / #1397: The message must be operator-actionable and must not expose
        // internal implementation details (e.g. C# method names). When the wizard
        // passes ?agentImage=... the message references it specifically so operators
        // understand exactly which image triggered the mismatch and how to fix it.
        if (provider == ProviderAnthropic && path == CredentialDispatchPath.Rest)
        {
            // #1397: if the caller supplied the chosen agent image, reference it in the
            // remediation copy so the operator can correlate the banner to their selection.
            var imageClause = !string.IsNullOrWhiteSpace(agentImage)
                ? $"The selected agent image `{agentImage.Trim()}` uses the Claude Code in-container path, " +
                  "which requires a Claude.ai OAuth token — but the stored credential is an " +
                  "Anthropic Platform API key (sk-ant-api…). "
                : string.Empty;

            if (!string.IsNullOrWhiteSpace(agentImage))
            {
                // Image-specific copy: operator picked an image whose runtime requires
                // the REST path, but the stored credential is an OAuth token.
                return imageClause +
                    "To fix: either replace the 'anthropic-api-key' secret with an Anthropic Platform " +
                    "API key (sk-ant-api…) from console.anthropic.com so it works with the REST path, " +
                    "or pick an agent image that includes the `claude` CLI and use the Claude Code runtime " +
                    "with an OAuth token generated by `claude setup-token`.";
            }

            // Generic copy when no image context was provided.
            return "The stored credential is a Claude Code OAuth token, which the " +
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
        var imageSuffix = !string.IsNullOrWhiteSpace(agentImage)
            ? $" (agent image: `{agentImage.Trim()}`)"
            : string.Empty;
        return $"The stored {displayName} credential's format is not accepted by {pathLabel}{imageSuffix}. " +
            "Replace it with a credential in the expected format for this path, " +
            "or switch to a dispatch path that accepts the current format.";
    }

    /// <summary>
    /// Builds the per-path resolvability matrix for a credential the
    /// resolver has already produced. Each enum value of
    /// <see cref="CredentialDispatchPath"/> is evaluated against the
    /// adapter's <see cref="IModelProviderAdapter.IsCredentialFormatAccepted"/>;
    /// the result is reported as one row per path with a stable
    /// machine-readable label so the portal can render a matrix without
    /// hard-coding the per-path branching that lives in the adapter.
    /// </summary>
    /// <remarks>
    /// <para>
    /// #1714 step 8: under the strict per-path matrix every accepted
    /// credential is accepted on exactly one dispatch path. The summary
    /// is therefore either <c>path-specific</c> (one path accepts, the
    /// other rejects) or <c>format-rejected</c> (neither accepts). The
    /// per-path entries below carry the explicit "which path" detail
    /// so a portal banner can render which dispatch path the stored
    /// credential will work on.
    /// </para>
    /// </remarks>
    private static CredentialPathResolvability BuildPathResolvability(
        IModelProviderAdapter adapter,
        ModelProvider provider,
        string credential)
    {
        var restAccepted = adapter.IsCredentialFormatAccepted(
            provider, credential, AuthMethod.ApiKey);
        var agentRuntimeAccepted = adapter.IsCredentialFormatAccepted(
            provider, credential, AuthMethod.Oauth);

        // Strict per-path acceptance (#1714): exactly one path or none.
        // The legacy `all-paths` summary disappeared because Anthropic
        // credentials no longer cross-bind. The legacy
        // `in-container-cli-only` summary collapses into `path-specific`
        // — the explicit per-path entries below tell callers which
        // path is the accepting one.
        var summary = (restAccepted, agentRuntimeAccepted) switch
        {
            (false, false) => ReasonFormatRejected,
            _ => PathSourcePathSpecific,
        };

        return new CredentialPathResolvability(
            Summary: summary,
            Paths: new[]
            {
                new CredentialPathEntry(DispatchPathRest, restAccepted),
                new CredentialPathEntry(DispatchPathAgentRuntime, agentRuntimeAccepted),
            });
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

    private static bool TryParseAuthMethod(string? raw, out AuthMethod? method)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            method = null;
            return true;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case AuthMethodApiKey:
                method = AuthMethod.ApiKey;
                return true;
            case AuthMethodOauth:
                method = AuthMethod.Oauth;
                return true;
            default:
                method = null;
                return false;
        }
    }

    private static string FormatAuthMethod(AuthMethod method) =>
        method switch
        {
            AuthMethod.ApiKey => AuthMethodApiKey,
            AuthMethod.Oauth => AuthMethodOauth,
            _ => method.ToString(),
        };

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
/// its format is accepted by the auth method selected via
/// <c>?authMethod</c> (or, for legacy callers, the auth method derived
/// from <c>?dispatchPath</c>). For Anthropic/OpenAI/Google: a non-empty
/// secret exists at unit or tenant scope, its ciphertext authenticates,
/// and the provider adapter's pre-flight format check clears. For
/// Ollama: <c>true</c> when the configured base URL responded to a
/// health probe.
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
/// requested auth method — for example an Anthropic API key stored in
/// the Claude Code OAuth slot). <c>null</c> when
/// resolvable. The portal uses this to pick a specific banner copy;
/// additional codes may be appended in later waves.
/// </param>
/// <param name="Paths">
/// Per-path resolvability matrix for the stored credential (#1690).
/// <c>null</c> when no credential is configured (the
/// <see cref="Resolvable"/>/<see cref="Reason"/> fields already carry
/// the only signal available); populated when a credential decrypts so
/// the portal can render which dispatch paths will accept it. The
/// scalar <see cref="Resolvable"/> + <see cref="Source"/> fields stay
/// the canonical "yes/no" answer for the auth method the caller asked
/// about via <c>?authMethod=</c>; <see cref="Paths"/> is the richer view
/// that decouples per-shape dispatch capability from per-call
/// evaluation.
/// </param>
public record ProviderCredentialStatusResponse(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("resolvable")] bool Resolvable,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("suggestion")] string? Suggestion,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("paths")] CredentialPathResolvability? Paths = null);

/// <summary>
/// Per-dispatch-path resolvability matrix for a stored credential (#1690).
/// </summary>
/// <param name="Summary">
/// Stable machine-readable label collapsing the per-path entries into
/// a portal-renderable shorthand. Values:
/// <list type="bullet">
///   <item><c>"path-specific"</c> — exactly one dispatch path accepts the stored credential (the per-path entries name which one). Under #1714's strict per-path acceptance, every recognised credential shape is accepted on exactly one path: Anthropic OAuth on the in-container Claude path, Anthropic API keys on the Spring Voyage REST path.</item>
///   <item><c>"format-rejected"</c> — neither path accepts the credential's shape.</item>
/// </list>
/// New paths added to <see cref="CredentialDispatchPath"/> appear in
/// <see cref="Paths"/> automatically.
/// </param>
/// <param name="Paths">
/// Explicit per-path acceptance list. Each entry names a path and
/// reports whether the runtime's pre-flight format check accepts the
/// stored credential on that path. The list is exhaustive across the
/// runtime's supported paths.
/// </param>
public record CredentialPathResolvability(
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("paths")] IReadOnlyList<CredentialPathEntry> Paths);

/// <summary>
/// One row of <see cref="CredentialPathResolvability.Paths"/>: a
/// dispatch-path label plus the runtime's acceptance verdict.
/// </summary>
/// <param name="Path">
/// Wire-stable dispatch path identifier — mirrors the <c>?dispatchPath=</c>
/// query parameter. Today: <c>"rest"</c> or <c>"agent-runtime"</c>.
/// </param>
/// <param name="Accepted">
/// <c>true</c> when the runtime's pre-flight format check accepts the
/// stored credential on this path (no network round-trip is performed —
/// this is shape-only). <c>false</c> when the path is known to reject
/// it (e.g. the Anthropic Platform REST endpoint rejects OAuth tokens).
/// </param>
public record CredentialPathEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("accepted")] bool Accepted);
