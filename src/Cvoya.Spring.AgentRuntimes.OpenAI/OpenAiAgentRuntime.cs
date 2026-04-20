// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.OpenAI;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.AgentRuntimes;

using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="IAgentRuntime"/> for the OpenAI Platform API combined with the
/// in-process <c>dapr-agent</c> execution tool. The runtime advertises
/// itself as <see cref="Id"/>=<c>openai</c> and <see cref="ToolKind"/>=
/// <c>dapr-agent</c>, validates credentials by issuing a read-only
/// <c>GET /v1/models</c> against <see cref="DefaultBaseUrl"/>, and seeds
/// its model catalog from the runtime's <c>agent-runtimes/openai/seed.json</c>
/// file (see <see cref="OpenAiAgentRuntimeSeed"/>).
/// </summary>
/// <remarks>
/// <para>
/// The runtime is registered as a singleton and is safe to share across
/// concurrent requests. The per-request HTTP client is taken from
/// <see cref="HttpClientName"/> on the injected <see cref="IHttpClientFactory"/>
/// so the host-wide handler lifecycle is honoured.
/// </para>
/// <para>
/// During Phase 2 of the #674 refactor this runtime co-exists with the
/// hardcoded OpenAI paths in
/// <c>Cvoya.Spring.Dapr.Execution.ProviderCredentialValidator</c> and
/// <c>ModelCatalog.StaticFallback</c>; those paths are removed by the
/// Phase 3 wizard issue.
/// </para>
/// </remarks>
public class OpenAiAgentRuntime : IAgentRuntime
{
    /// <summary>The named <see cref="HttpClient"/> the runtime resolves from <see cref="IHttpClientFactory"/>.</summary>
    public const string HttpClientName = "Cvoya.Spring.AgentRuntimes.OpenAI";

    /// <summary>The OpenAI Platform API base URL used when the seed does not pin a value.</summary>
    public const string DefaultBaseUrl = "https://api.openai.com";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAiAgentRuntime> _logger;
    private readonly Lazy<OpenAiAgentRuntimeSeed> _seed;
    private readonly Lazy<IReadOnlyList<ModelDescriptor>> _defaultModels;

    /// <summary>
    /// Creates a runtime that loads its seed from the assembly directory
    /// (the standard production path).
    /// </summary>
    /// <param name="httpClientFactory">Factory for the outbound HTTP client used to validate credentials.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    public OpenAiAgentRuntime(
        IHttpClientFactory httpClientFactory,
        ILogger<OpenAiAgentRuntime> logger)
        : this(httpClientFactory, logger, OpenAiAgentRuntimeSeedLoader.LoadFromAssemblyDirectory)
    {
    }

    /// <summary>
    /// Test/advanced-composition constructor. Accepts a seed factory so
    /// tests can supply an in-memory seed without touching the file system.
    /// </summary>
    /// <param name="httpClientFactory">Factory for the outbound HTTP client used to validate credentials.</param>
    /// <param name="logger">Logger for diagnostic output.</param>
    /// <param name="seedFactory">Factory invoked once on first access to produce the seed payload.</param>
    internal OpenAiAgentRuntime(
        IHttpClientFactory httpClientFactory,
        ILogger<OpenAiAgentRuntime> logger,
        Func<OpenAiAgentRuntimeSeed> seedFactory)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(seedFactory);

        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _seed = new Lazy<OpenAiAgentRuntimeSeed>(seedFactory, isThreadSafe: true);
        _defaultModels = new Lazy<IReadOnlyList<ModelDescriptor>>(
            () => _seed.Value.Models
                .Select(id => new ModelDescriptor(id, id, ContextWindow: null))
                .ToArray(),
            isThreadSafe: true);
    }

    /// <inheritdoc />
    public string Id => "openai";

    /// <inheritdoc />
    public string DisplayName => "OpenAI (dapr-agent + OpenAI API)";

    /// <inheritdoc />
    public string ToolKind => "dapr-agent";

    /// <inheritdoc />
    public AgentRuntimeCredentialSchema CredentialSchema { get; } = new(
        AgentRuntimeCredentialKind.ApiKey,
        DisplayHint: "OpenAI Platform API key — typically starts with 'sk-' (https://platform.openai.com/api-keys).");

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
                ErrorMessage: "Supply an OpenAI API key to validate.",
                Status: CredentialValidationStatus.Invalid);
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{EffectiveBaseUrl}/v1/models");
        request.Headers.Add("Authorization", $"Bearer {credential}");

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
            // (OpenAI returns a JSON envelope with `error.message` for most
            // failure modes). The body can be empty — in that case fall back
            // to the status text.
            var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

            // 5xx is treated as a transient transport problem, not a key
            // rejection — the credential's validity is unknown until the
            // service recovers.
            if ((int)response.StatusCode >= 500)
            {
                _logger.LogWarning(
                    "OpenAI /v1/models returned {StatusCode} during credential validation; treating as NetworkError. Body: {Body}",
                    response.StatusCode, body);
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
                "Network error contacting OpenAI /v1/models during credential validation.");
            return new CredentialValidationResult(
                Valid: false,
                ErrorMessage: $"Could not reach the OpenAI API: {ex.Message}",
                Status: CredentialValidationStatus.NetworkError);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex,
                "Timeout contacting OpenAI /v1/models during credential validation.");
            return new CredentialValidationResult(
                Valid: false,
                ErrorMessage: "Timed out contacting the OpenAI API.",
                Status: CredentialValidationStatus.NetworkError);
        }
    }

    /// <inheritdoc />
    public async Task<FetchLiveModelsResult> FetchLiveModelsAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            return FetchLiveModelsResult.InvalidCredential(
                "Supply an OpenAI API key to fetch the live model catalog.");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{EffectiveBaseUrl}/v1/models");
        request.Headers.Add("Authorization", $"Bearer {credential}");

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content
                    .ReadFromJsonAsync(
                        OpenAiModelsJsonContext.Default.OpenAiModelsResponse,
                        cancellationToken)
                    .ConfigureAwait(false);
                var models = BuildModels(payload);
                return FetchLiveModelsResult.Success(models);
            }

            var body = await SafeReadBodyAsync(response, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return FetchLiveModelsResult.InvalidCredential(
                    BuildErrorMessage(response.StatusCode, body, transientPrefix: false));
            }

            if ((int)response.StatusCode >= 500)
            {
                _logger.LogWarning(
                    "OpenAI /v1/models returned {StatusCode} during live-model fetch; treating as NetworkError. Body: {Body}",
                    response.StatusCode, body);
                return FetchLiveModelsResult.NetworkError(
                    BuildErrorMessage(response.StatusCode, body, transientPrefix: true));
            }

            // Other 4xx — likely a key scoping issue that the operator
            // should surface as an invalid credential. The JSON body
            // carries the precise reason.
            return FetchLiveModelsResult.InvalidCredential(
                BuildErrorMessage(response.StatusCode, body, transientPrefix: false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex,
                "Network error contacting OpenAI /v1/models during live-model fetch.");
            return FetchLiveModelsResult.NetworkError(
                $"Could not reach the OpenAI API: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex,
                "Timeout contacting OpenAI /v1/models during live-model fetch.");
            return FetchLiveModelsResult.NetworkError(
                "Timed out contacting the OpenAI API.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse OpenAI /v1/models response.");
            return FetchLiveModelsResult.NetworkError(
                "The OpenAI API returned an unexpected response body.");
        }
    }

    private static IReadOnlyList<ModelDescriptor> BuildModels(OpenAiModelsResponse? payload)
    {
        if (payload?.Data is null || payload.Data.Length == 0)
        {
            return Array.Empty<ModelDescriptor>();
        }

        var result = new List<ModelDescriptor>(payload.Data.Length);
        foreach (var entry in payload.Data)
        {
            if (string.IsNullOrWhiteSpace(entry.Id))
            {
                continue;
            }
            // OpenAI's /v1/models envelope does not publish a context
            // window — DisplayName mirrors Id for parity with the seed
            // catalog projection.
            result.Add(new ModelDescriptor(entry.Id, entry.Id, ContextWindow: null));
        }
        return result;
    }

    /// <inheritdoc />
    public Task<ContainerBaselineCheckResult> VerifyContainerBaselineAsync(
        CancellationToken cancellationToken = default)
    {
        // The dapr-agent execution tool is implemented in-process by
        // Cvoya.Spring.Dapr.Execution.DaprAgentLauncher and depends on the
        // host having a Dapr sidecar reachable for Conversation API calls
        // and on outbound HTTPS reachability to api.openai.com. Both are
        // host-wide concerns that are checked elsewhere (Dapr health probes
        // / startup configuration report). At the runtime level we only
        // need to confirm the Dapr Actors SDK is loaded into the runtime
        // process, which is the dependency that uniquely belongs to this
        // tool kind.
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
            ? $"OpenAI returned a transient HTTP {(int)statusCode} {statusCode}"
            : $"OpenAI rejected the credential (HTTP {(int)statusCode} {statusCode})";

        return string.IsNullOrEmpty(body)
            ? $"{prefix}."
            : $"{prefix}: {body}";
    }
}

/// <summary>Subset of OpenAI's <c>GET /v1/models</c> envelope we parse during live-model fetch.</summary>
internal sealed record OpenAiModelsResponse(
    [property: JsonPropertyName("data")] OpenAiModelDto[]? Data);

/// <summary>One entry in the OpenAI models envelope.</summary>
internal sealed record OpenAiModelDto(
    [property: JsonPropertyName("id")] string? Id);

[JsonSerializable(typeof(OpenAiModelsResponse))]
internal partial class OpenAiModelsJsonContext : JsonSerializerContext;