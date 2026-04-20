// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Google;

using System.Net;

using Cvoya.Spring.Core.AgentRuntimes;

using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="IAgentRuntime"/> for the Google AI (Generative Language) API
/// combined with the in-process <c>dapr-agent</c> execution tool. The runtime
/// advertises itself as <see cref="Id"/>=<c>google</c> and
/// <see cref="ToolKind"/>=<c>dapr-agent</c>, validates credentials by issuing
/// a read-only <c>GET /v1beta/models</c> against <see cref="DefaultBaseUrl"/>,
/// and seeds its model catalog from the runtime's
/// <c>agent-runtimes/google/seed.json</c> file (see
/// <see cref="GoogleAgentRuntimeSeed"/>).
/// </summary>
/// <remarks>
/// <para>
/// The runtime is registered as a singleton and is safe to share across
/// concurrent requests. The per-request HTTP client is taken from
/// <see cref="HttpClientName"/> on the injected <see cref="IHttpClientFactory"/>
/// so the host-wide handler lifecycle is honoured.
/// </para>
/// <para>
/// Google's API authenticates with a query-string <c>?key=</c> rather than a
/// header — credentials never appear in log lines because the URL is built
/// inside <see cref="ValidateCredentialAsync"/> and the named HTTP client
/// emits diagnostics through the standard logging pipeline (which only
/// captures the request URI when explicitly enabled). The validator returns
/// <see cref="CredentialValidationStatus.Invalid"/> for 4xx responses and
/// <see cref="CredentialValidationStatus.NetworkError"/> for transport
/// failures and 5xx responses, matching the contract documented on
/// <see cref="IAgentRuntime.ValidateCredentialAsync"/>.
/// </para>
/// <para>
/// During Phase 2 of the #674 refactor this runtime co-exists with the
/// hardcoded Google paths in
/// <c>Cvoya.Spring.Dapr.Execution.ProviderCredentialValidator</c> and
/// <c>ModelCatalog.StaticFallback</c>; those paths are removed by the
/// Phase 3 wizard issue.
/// </para>
/// </remarks>
public class GoogleAgentRuntime : IAgentRuntime
{
    /// <summary>The named <see cref="HttpClient"/> the runtime resolves from <see cref="IHttpClientFactory"/>.</summary>
    public const string HttpClientName = "Cvoya.Spring.AgentRuntimes.Google";

    /// <summary>The Google AI (Generative Language) API base URL used when the seed does not pin a value.</summary>
    public const string DefaultBaseUrl = "https://generativelanguage.googleapis.com";

    /// <summary>The path of the credential-validation endpoint relative to <see cref="DefaultBaseUrl"/>.</summary>
    internal const string ValidationPath = "/v1beta/models";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GoogleAgentRuntime> _logger;
    private readonly Lazy<GoogleAgentRuntimeSeed> _seed;
    private readonly Lazy<IReadOnlyList<ModelDescriptor>> _defaultModels;

    /// <summary>
    /// Creates a runtime that loads its seed from the assembly directory
    /// (the standard production path).
    /// </summary>
    /// <param name="httpClientFactory">Factory for the outbound HTTP client used to validate credentials.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public GoogleAgentRuntime(
        IHttpClientFactory httpClientFactory,
        ILogger<GoogleAgentRuntime> logger)
        : this(httpClientFactory, logger, GoogleAgentRuntimeSeedLoader.LoadFromAssemblyDirectory)
    {
    }

    /// <summary>
    /// Test/advanced-composition constructor. Accepts a seed factory so
    /// tests can supply an in-memory seed without touching the file system.
    /// </summary>
    /// <param name="httpClientFactory">Factory for the outbound HTTP client used to validate credentials.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="seedFactory">Factory invoked once on first access to produce the seed payload.</param>
    internal GoogleAgentRuntime(
        IHttpClientFactory httpClientFactory,
        ILogger<GoogleAgentRuntime> logger,
        Func<GoogleAgentRuntimeSeed> seedFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(seedFactory);

        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _seed = new Lazy<GoogleAgentRuntimeSeed>(seedFactory, isThreadSafe: true);
        _defaultModels = new Lazy<IReadOnlyList<ModelDescriptor>>(
            () => _seed.Value.Models
                .Select(id => new ModelDescriptor(id, id, ContextWindow: null))
                .ToArray(),
            isThreadSafe: true);
    }

    /// <inheritdoc />
    public string Id => "google";

    /// <inheritdoc />
    public string DisplayName => "Google AI (dapr-agent + Google AI API)";

    /// <inheritdoc />
    public string ToolKind => "dapr-agent";

    /// <inheritdoc />
    public AgentRuntimeCredentialSchema CredentialSchema { get; } = new(
        AgentRuntimeCredentialKind.ApiKey,
        DisplayHint: "Google AI Studio API key — generate one at https://aistudio.google.com/apikey. Requires the Generative Language API.");

    /// <inheritdoc />
    public IReadOnlyList<ModelDescriptor> DefaultModels => _defaultModels.Value;

    /// <summary>
    /// The base URL declared by the seed file; falls back to
    /// <see cref="DefaultBaseUrl"/> when the seed does not pin a value.
    /// </summary>
    internal string EffectiveBaseUrl =>
        string.IsNullOrWhiteSpace(_seed.Value.BaseUrl)
            ? DefaultBaseUrl
            : _seed.Value.BaseUrl!.TrimEnd('/');

    /// <inheritdoc />
    public async Task<CredentialValidationResult> ValidateCredentialAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            return new CredentialValidationResult(
                Valid: false,
                ErrorMessage: "Supply a Google AI API key to validate.",
                Status: CredentialValidationStatus.Invalid);
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var uri = $"{EffectiveBaseUrl}{ValidationPath}?key={Uri.EscapeDataString(credential)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                return new CredentialValidationResult(
                    Valid: true,
                    ErrorMessage: null,
                    Status: CredentialValidationStatus.Valid);
            }

            // Read the response body so the operator gets a precise reason
            // (Google returns a JSON envelope with `error.message` for most
            // failure modes). The body can be empty — fall back to the
            // status text in that case.
            var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

            // 5xx is treated as a transient transport problem, not a key
            // rejection — the credential's validity is unknown until the
            // service recovers.
            if ((int)response.StatusCode >= 500)
            {
                _logger.LogWarning(
                    "Google {Path} returned {StatusCode} during credential validation; treating as NetworkError. Body: {Body}",
                    ValidationPath, response.StatusCode, body);
                return new CredentialValidationResult(
                    Valid: false,
                    ErrorMessage: BuildErrorMessage(response.StatusCode, body, transientPrefix: true),
                    Status: CredentialValidationStatus.NetworkError);
            }

            return new CredentialValidationResult(
                Valid: false,
                ErrorMessage: BuildErrorMessage(response.StatusCode, body, transientPrefix: false),
                Status: CredentialValidationStatus.Invalid);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "Network error contacting Google {Path} during credential validation.", ValidationPath);
            return new CredentialValidationResult(
                Valid: false,
                ErrorMessage: $"Could not reach the Google AI API: {ex.Message}",
                Status: CredentialValidationStatus.NetworkError);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex,
                "Timeout contacting Google {Path} during credential validation.", ValidationPath);
            return new CredentialValidationResult(
                Valid: false,
                ErrorMessage: "Timed out contacting the Google AI API.",
                Status: CredentialValidationStatus.NetworkError);
        }
    }

    /// <inheritdoc />
    public Task<ContainerBaselineCheckResult> VerifyContainerBaselineAsync(
        CancellationToken cancellationToken = default)
    {
        // The dapr-agent execution tool is implemented in-process by
        // Cvoya.Spring.Dapr.Execution.DaprAgentLauncher and depends on the
        // host having a Dapr sidecar reachable for Conversation API calls
        // and on outbound HTTPS reachability to generativelanguage.googleapis.com.
        // Both are host-wide concerns that are checked elsewhere (Dapr health
        // probes / startup configuration report). At the runtime level we
        // only need to confirm the Dapr Actors SDK is loaded into the
        // runtime process, which is the dependency that uniquely belongs
        // to this tool kind.
        var errors = new List<string>();

        if (!IsDaprActorsAssemblyLoaded())
        {
            errors.Add(
                "dapr-agent baseline check: the 'Dapr.Actors' assembly is not loaded in the host process. " +
                "Reference 'Dapr.Actors' (or call 'AddCvoyaSpringDapr') so the dapr-agent launcher can dispatch agent invocations.");
        }

        var result = errors.Count == 0
            ? new ContainerBaselineCheckResult(true, Array.Empty<string>())
            : new ContainerBaselineCheckResult(false, errors);

        return Task.FromResult(result);
    }

    private static bool IsDaprActorsAssemblyLoaded()
    {
        // Intentionally loaded by name (not via a typeof) so this project
        // doesn't have to take a hard NuGet dependency on Dapr.Actors. The
        // dapr-agent launcher lives in Cvoya.Spring.Dapr, which references
        // Dapr.Actors — when that assembly is present in the host's load
        // context, the baseline is satisfied.
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = assembly.GetName().Name;
            if (string.Equals(name, "Dapr.Actors", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<string> SafeReadBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body) ? string.Empty : body.Trim();
        }
        catch (Exception)
        {
            // The body is purely diagnostic — never let a read failure mask
            // the underlying status code.
            return string.Empty;
        }
    }

    private static string BuildErrorMessage(HttpStatusCode statusCode, string body, bool transientPrefix)
    {
        var prefix = transientPrefix
            ? $"Google returned a transient HTTP {(int)statusCode} {statusCode}"
            : $"Google rejected the credential (HTTP {(int)statusCode} {statusCode})";

        return string.IsNullOrEmpty(body)
            ? $"{prefix}."
            : $"{prefix}: {body}";
    }
}