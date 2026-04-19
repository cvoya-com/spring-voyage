// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

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

    /// <summary>
    /// Registers the system-level endpoints on <paramref name="app"/>.
    /// </summary>
    public static RouteGroupBuilder MapSystemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/system")
            .WithTags("System");

        group.MapGet("/credentials/{provider}/status", GetCredentialStatusAsync)
            .WithName("GetProviderCredentialStatus")
            .WithSummary("Report whether an LLM provider's credentials / endpoint are configured")
            .Produces<ProviderCredentialStatusResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);

        group.MapPost("/credentials/{provider}/validate", ValidateCredentialAsync)
            .WithName("ValidateProviderCredential")
            .WithSummary("Validate a caller-supplied LLM provider API key against the provider")
            .Produces<ProviderCredentialValidationResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);

        return group;
    }

    private static async Task<IResult> GetCredentialStatusAsync(
        string provider,
        ILlmCredentialResolver credentialResolver,
        IHttpClientFactory httpClientFactory,
        IOptions<OllamaOptions> ollamaOptions,
        CancellationToken cancellationToken)
    {
        var normalized = (provider ?? string.Empty).Trim().ToLowerInvariant();

        switch (normalized)
        {
            case ProviderAnthropic:
            case ProviderOpenAi:
            case ProviderGoogle:
                {
                    // No unit context — the wizard runs before the unit
                    // exists. Resolver will fall through to the tenant-
                    // scope secret, which is what the wizard cares about.
                    var resolution = await credentialResolver.ResolveAsync(
                        normalized,
                        unitName: null,
                        cancellationToken);

                    var resolvable = resolution.Value is { Length: > 0 };
                    var source = resolvable
                        ? MapSource(resolution.Source)
                        : null;
                    var suggestion = resolvable
                        ? null
                        : BuildCredentialSuggestion(normalized, resolution.SecretName);

                    // NEVER include `resolution.Value` in the response —
                    // the endpoint is read-by-anyone (within the tenant)
                    // and the key material must stay server-side.
                    return Results.Ok(new ProviderCredentialStatusResponse(
                        Provider: normalized,
                        Resolvable: resolvable,
                        Source: source,
                        Suggestion: suggestion));
                }
            case ProviderOllama:
                {
                    var baseUrl = ollamaOptions.Value.BaseUrl.TrimEnd('/');
                    var (reachable, reason) = await ProbeOllamaAsync(
                        httpClientFactory,
                        baseUrl,
                        ollamaOptions.Value.HealthCheckTimeoutSeconds,
                        cancellationToken);

                    var suggestion = reachable
                        ? null
                        : $"Ollama not reachable at {baseUrl}. Check that the Ollama server is running. ({reason})";

                    return Results.Ok(new ProviderCredentialStatusResponse(
                        Provider: normalized,
                        Resolvable: reachable,
                        // Ollama has no tenant/unit secret — the reachability
                        // of the configured endpoint is deployment config
                        // (tier-1), so Source is always null.
                        Source: null,
                        Suggestion: suggestion));
                }
            default:
                return Results.BadRequest(new
                {
                    error = "unknown-provider",
                    message = "Provider must be one of: anthropic, openai, google, ollama.",
                });
        }
    }

    private static async Task<IResult> ValidateCredentialAsync(
        string provider,
        ValidateCredentialRequest? request,
        IProviderCredentialValidator validator,
        CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.ApiKey))
        {
            return Results.BadRequest(new
            {
                error = "missing-key",
                message = "Request body must include a non-empty 'apiKey'.",
            });
        }

        var result = await validator.ValidateAsync(provider, request.ApiKey, cancellationToken);

        if (result.Status == ProviderCredentialValidationStatus.UnknownProvider)
        {
            // Unknown-provider is structurally a 400 — the caller asked us
            // to validate against a route we do not serve. All other
            // statuses (including Unauthorized and NetworkError) ride a
            // 200 with `valid=false` so the portal's validation UI can
            // treat them uniformly without catching HTTP errors.
            return Results.BadRequest(new
            {
                error = "unknown-provider",
                message = result.ErrorMessage ?? "Provider must be one of: anthropic, openai, google.",
            });
        }

        var status = result.Status switch
        {
            ProviderCredentialValidationStatus.Valid => "valid",
            ProviderCredentialValidationStatus.Unauthorized => "unauthorized",
            ProviderCredentialValidationStatus.ProviderError => "provider-error",
            ProviderCredentialValidationStatus.NetworkError => "network-error",
            ProviderCredentialValidationStatus.MissingKey => "missing-key",
            _ => "unknown",
        };

        // NEVER echo `request.ApiKey` in the response. The shape is
        // limited to the validation verdict, an optional model list, and
        // an error string — the key stays on the request side only.
        return Results.Ok(new ProviderCredentialValidationResponse(
            Provider: (provider ?? string.Empty).Trim().ToLowerInvariant(),
            Valid: result.Status == ProviderCredentialValidationStatus.Valid,
            Status: status,
            Models: result.Status == ProviderCredentialValidationStatus.Valid
                ? (result.Models ?? Array.Empty<string>())
                : null,
            Error: result.ErrorMessage));
    }

    private static string? MapSource(LlmCredentialSource source) => source switch
    {
        LlmCredentialSource.Unit => "unit",
        LlmCredentialSource.Tenant => "tenant",
        _ => null,
    };

    private static string BuildCredentialSuggestion(string provider, string secretName)
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
        return $"{displayName} credentials are not configured. " +
            $"Set the tenant-default secret '{secretName}' from Settings → Tenant defaults, " +
            $"or create a unit-scoped override of the same name.";
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
/// <c>true</c> when the platform can obtain the credential (for
/// Anthropic/OpenAI/Google: a non-empty secret exists at unit or
/// tenant scope). For Ollama: <c>true</c> when the configured base URL
/// responded to a health probe.
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
public record ProviderCredentialStatusResponse(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("resolvable")] bool Resolvable,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("suggestion")] string? Suggestion);

/// <summary>
/// Request body for <c>POST /api/v1/system/credentials/{provider}/validate</c>.
/// </summary>
/// <param name="ApiKey">The plaintext API key to validate. Never persisted.</param>
public record ValidateCredentialRequest(
    [property: JsonPropertyName("apiKey")] string ApiKey);

/// <summary>
/// Response body for <c>POST /api/v1/system/credentials/{provider}/validate</c>.
/// </summary>
/// <param name="Provider">Echoes the requested provider id.</param>
/// <param name="Valid">
/// <c>true</c> when the provider accepted the key. The portal gates the
/// wizard's Next button on this flag.
/// </param>
/// <param name="Status">
/// Coarse-grained outcome: <c>"valid"</c>, <c>"unauthorized"</c>,
/// <c>"provider-error"</c>, <c>"network-error"</c>, <c>"missing-key"</c>.
/// </param>
/// <param name="Models">
/// Model ids returned by the provider when <see cref="Valid"/> is
/// <c>true</c>, so the wizard can seed the Model dropdown from the same
/// call it used to validate the key. <c>null</c> on any failure.
/// </param>
/// <param name="Error">
/// Operator-facing failure reason. <c>null</c> on success. NEVER
/// contains the key itself.
/// </param>
public record ProviderCredentialValidationResponse(
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("valid")] bool Valid,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("models")] IReadOnlyList<string>? Models,
    [property: JsonPropertyName("error")] string? Error);