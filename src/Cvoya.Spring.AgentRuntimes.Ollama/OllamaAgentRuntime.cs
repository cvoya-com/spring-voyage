// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Ollama;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.AgentRuntimes;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// <see cref="IAgentRuntime"/> implementation for the local Ollama endpoint
/// running through the <c>dapr-agent</c> execution tool. Targets developer
/// laptops and air-gapped deployments where the LLM is hosted on the host
/// machine (or a sidecar container) and reached without authentication.
/// </summary>
/// <remarks>
/// <para>
/// The runtime publishes a stable <see cref="Id"/> of <c>ollama</c>; this
/// value is persisted on tenant installs and unit bindings so a future
/// rename would invalidate every existing record. Treat it as immutable.
/// </para>
/// <para>
/// <see cref="CredentialSchema"/> reports
/// <see cref="AgentRuntimeCredentialKind.None"/> — the typical local Ollama
/// install requires no API key. <see cref="ValidateCredentialAsync"/>
/// therefore ignores the supplied credential and probes the configured
/// endpoint's <c>/api/tags</c> route to confirm reachability instead.
/// Network failures surface as
/// <see cref="CredentialValidationStatus.NetworkError"/> per the
/// <see cref="IAgentRuntime"/> contract — the method never throws.
/// </para>
/// <para>
/// <see cref="DefaultModels"/> is loaded once at construction from the
/// runtime's embedded <c>agent-runtimes/ollama/seed.json</c> catalog. The
/// list mirrors the curated Ollama family supported by the OSS deployment;
/// tenants may extend it via per-install configuration.
/// </para>
/// <para>
/// <see cref="VerifyContainerBaselineAsync"/> reports two things: that the
/// <c>dapr-agent</c> tool kind is the runtime's expected execution path
/// (informational), and that the configured Ollama endpoint is reachable.
/// The Ollama probe is best-effort — operators sometimes deploy the
/// runtime before the Ollama server boots, so an unreachable endpoint at
/// install time is reported as a non-fatal error string the wizard can
/// surface alongside a "retry" affordance.
/// </para>
/// </remarks>
public class OllamaAgentRuntime : IAgentRuntime
{
    /// <summary>
    /// The runtime's stable identifier, persisted in tenant installs and
    /// unit bindings. Kept as a constant so external code (CLI, wizard,
    /// tests) can reference it without taking a runtime dependency.
    /// </summary>
    public const string RuntimeId = "ollama";

    /// <summary>
    /// The execution-tool identifier the runtime delegates to. Shared with
    /// other dapr-agent-backed runtimes so the host can reason about
    /// container-baseline requirements without enumerating every runtime.
    /// </summary>
    public const string DaprAgentToolKind = "dapr-agent";

    /// <summary>
    /// The named <see cref="HttpClient"/> the runtime uses for outbound
    /// probes. Registered by the runtime's DI extension; resolved on each
    /// call so a test harness can swap the handler chain without
    /// reconstructing the runtime.
    /// </summary>
    public const string HttpClientName = "Cvoya.Spring.AgentRuntimes.Ollama";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<OllamaAgentRuntimeOptions> _options;
    private readonly ILogger<OllamaAgentRuntime> _logger;
    private readonly Lazy<IReadOnlyList<ModelDescriptor>> _defaultModels;

    /// <summary>
    /// Constructs the runtime with the dependencies provided by DI.
    /// </summary>
    /// <param name="httpClientFactory">Factory used to obtain the named HTTP client for the reachability probe.</param>
    /// <param name="options">Configuration for the Ollama endpoint (base URL, probe timeout).</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public OllamaAgentRuntime(
        IHttpClientFactory httpClientFactory,
        IOptions<OllamaAgentRuntimeOptions> options,
        ILogger<OllamaAgentRuntime>? logger = null)
    {
        _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<OllamaAgentRuntime>.Instance;

        // Loaded lazily so the constructor remains cheap and so a packaging
        // defect (missing seed file) surfaces only when a caller actually
        // touches the catalog — keeps DI graph construction healthy.
        _defaultModels = new Lazy<IReadOnlyList<ModelDescriptor>>(
            () => OllamaSeed.ToDescriptors(OllamaSeed.Load()),
            isThreadSafe: true);
    }

    /// <inheritdoc />
    public string Id => RuntimeId;

    /// <inheritdoc />
    public string DisplayName => "Ollama (dapr-agent + local Ollama)";

    /// <inheritdoc />
    public string ToolKind => DaprAgentToolKind;

    /// <inheritdoc />
    public AgentRuntimeCredentialSchema CredentialSchema { get; } = new(
        AgentRuntimeCredentialKind.None,
        DisplayHint: "Local Ollama installs require no credential. Set the base URL via the install's config_json.");

    /// <inheritdoc />
    public IReadOnlyList<ModelDescriptor> DefaultModels => _defaultModels.Value;

    /// <inheritdoc />
    public async Task<CredentialValidationResult> ValidateCredentialAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        // Ollama needs no credential; reachability is the actual signal the
        // wizard cares about. The `credential` argument is intentionally
        // unused — see CredentialSchema.Kind.
        _ = credential;

        var probe = await ProbeTagsEndpointAsync(cancellationToken).ConfigureAwait(false);

        return probe.Status switch
        {
            CredentialValidationStatus.Valid =>
                new CredentialValidationResult(true, null, CredentialValidationStatus.Valid),
            CredentialValidationStatus.Invalid =>
                new CredentialValidationResult(false, probe.Message, CredentialValidationStatus.Invalid),
            CredentialValidationStatus.NetworkError =>
                new CredentialValidationResult(false, probe.Message, CredentialValidationStatus.NetworkError),
            _ => new CredentialValidationResult(false, probe.Message, CredentialValidationStatus.Unknown),
        };
    }

    /// <inheritdoc />
    public async Task<FetchLiveModelsResult> FetchLiveModelsAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        // Ollama requires no credential; the supplied value is ignored.
        _ = credential;

        var baseUrl = _options.Value.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return FetchLiveModelsResult.NetworkError(
                "AgentRuntimes:Ollama:BaseUrl is empty.");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed))
        {
            return FetchLiveModelsResult.NetworkError(
                $"AgentRuntimes:Ollama:BaseUrl '{baseUrl}' is not a valid absolute URI.");
        }

        var uri = new Uri(parsed, "/api/tags");
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.Value.HealthCheckTimeoutSeconds));

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            using var response = await client.GetAsync(uri, cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                {
                    return FetchLiveModelsResult.InvalidCredential(
                        $"GET {uri} returned {(int)response.StatusCode} {response.StatusCode} — " +
                        "an Ollama reverse proxy is rejecting the request.");
                }
                return FetchLiveModelsResult.NetworkError(
                    $"GET {uri} returned {(int)response.StatusCode} {response.StatusCode}.");
            }

            var payload = await response.Content
                .ReadFromJsonAsync(OllamaTagsJsonContext.Default.OllamaTagsResponse, cts.Token)
                .ConfigureAwait(false);

            var models = BuildModels(payload);
            return FetchLiveModelsResult.Success(models);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return FetchLiveModelsResult.NetworkError(
                $"Probe of {uri} timed out after {timeout.TotalSeconds:0.#}s.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Ollama live-model fetch failed for {Uri}", uri);
            return FetchLiveModelsResult.NetworkError($"GET {uri} failed: {ex.Message}");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse Ollama /api/tags response.");
            return FetchLiveModelsResult.NetworkError(
                "The Ollama server returned an unexpected response body.");
        }
    }

    private static IReadOnlyList<ModelDescriptor> BuildModels(OllamaTagsResponse? payload)
    {
        if (payload?.Models is null || payload.Models.Length == 0)
        {
            return Array.Empty<ModelDescriptor>();
        }

        var result = new List<ModelDescriptor>(payload.Models.Length);
        foreach (var entry in payload.Models)
        {
            if (string.IsNullOrWhiteSpace(entry.Name))
            {
                continue;
            }
            result.Add(new ModelDescriptor(entry.Name!, entry.Name!, ContextWindow: null));
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<ContainerBaselineCheckResult> VerifyContainerBaselineAsync(
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>(capacity: 1);

        // The dapr-agent tool kind is supplied by the host's runtime layer,
        // not by this project. We cannot probe for the binary in a generic
        // way, so we surface a placeholder check: if the configured Ollama
        // endpoint is unreachable, that is the dominant baseline failure
        // operators care about. Hosts that ship dapr-agent inside the
        // container can extend the check via decorator/wrapper without
        // forking this class.
        var probe = await ProbeTagsEndpointAsync(cancellationToken).ConfigureAwait(false);
        if (probe.Status != CredentialValidationStatus.Valid)
        {
            errors.Add(
                $"Ollama endpoint '{_options.Value.BaseUrl}' is not reachable: {probe.Message}. " +
                "Start the Ollama server or override AgentRuntimes:Ollama:BaseUrl.");
        }

        return new ContainerBaselineCheckResult(errors.Count == 0, errors);
    }

    /// <summary>
    /// Issues a <c>GET {BaseUrl}/api/tags</c> probe against the configured
    /// Ollama endpoint. Maps the outcome to the
    /// <see cref="CredentialValidationStatus"/> vocabulary so callers can
    /// reuse the same projection in both
    /// <see cref="ValidateCredentialAsync"/> and
    /// <see cref="VerifyContainerBaselineAsync"/>.
    /// </summary>
    /// <param name="cancellationToken">Token to cancel the probe.</param>
    /// <returns>The probe outcome.</returns>
    protected virtual async Task<OllamaProbeResult> ProbeTagsEndpointAsync(
        CancellationToken cancellationToken)
    {
        var baseUrl = _options.Value.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return new OllamaProbeResult(
                CredentialValidationStatus.Invalid,
                "AgentRuntimes:Ollama:BaseUrl is empty.");
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed))
        {
            return new OllamaProbeResult(
                CredentialValidationStatus.Invalid,
                $"AgentRuntimes:Ollama:BaseUrl '{baseUrl}' is not a valid absolute URI.");
        }

        var probeUri = new Uri(parsed, "/api/tags");

        // Cap the timeout so a hung server doesn't pin the wizard's spinner.
        // Default of 5s is generous for a local network round-trip.
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.Value.HealthCheckTimeoutSeconds));

        var client = _httpClientFactory.CreateClient(HttpClientName);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            using var response = await client.GetAsync(probeUri, cts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return new OllamaProbeResult(CredentialValidationStatus.Valid, null);
            }

            // 401/403 against `/api/tags` is unusual but possible behind a
            // reverse proxy that requires auth — surface as Invalid so the
            // wizard explains the credential mismatch rather than treating
            // it as a transient network failure.
            var status = response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                ? CredentialValidationStatus.Invalid
                : CredentialValidationStatus.NetworkError;

            return new OllamaProbeResult(
                status,
                $"GET {probeUri} returned {(int)response.StatusCode} {response.StatusCode}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancellation — propagate.
            throw;
        }
        catch (OperationCanceledException)
        {
            return new OllamaProbeResult(
                CredentialValidationStatus.NetworkError,
                $"Probe of {probeUri} timed out after {timeout.TotalSeconds:0.#}s.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogDebug(ex, "Ollama reachability probe failed for {ProbeUri}", probeUri);
            return new OllamaProbeResult(
                CredentialValidationStatus.NetworkError,
                $"GET {probeUri} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Projection of an <c>/api/tags</c> probe outcome onto the contract's
    /// status vocabulary, with an optional human-readable message.
    /// </summary>
    /// <param name="Status">The outcome class — <c>Valid</c> on a 2xx, <c>Invalid</c> for misconfiguration or 401/403, <c>NetworkError</c> for transport failure.</param>
    /// <param name="Message">A human-readable description, or <c>null</c> when the probe succeeded.</param>
    protected sealed record OllamaProbeResult(
        CredentialValidationStatus Status,
        string? Message);
}

/// <summary>Subset of Ollama's <c>GET /api/tags</c> envelope we parse during live-model fetch.</summary>
internal sealed record OllamaTagsResponse(
    [property: JsonPropertyName("models")] OllamaTagsModelDto[]? Models);

/// <summary>One entry in the Ollama tags response.</summary>
internal sealed record OllamaTagsModelDto(
    [property: JsonPropertyName("name")] string? Name);

[JsonSerializable(typeof(OllamaTagsResponse))]
internal partial class OllamaTagsJsonContext : JsonSerializerContext;