// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Ollama;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

/// <summary>
/// <see cref="IAgentRuntime"/> implementation for the local Ollama endpoint
/// running through the <c>dapr-agent</c> execution tool. Updated in T-03
/// (#945) to emit an in-container probe plan (no credential step; Ollama
/// runs credential-less).
/// </summary>
/// <remarks>
/// <para>
/// The runtime publishes a stable <see cref="Id"/> of <c>ollama</c>; this
/// value is persisted on tenant installs and unit bindings so a future
/// rename would invalidate every existing record. Treat it as immutable.
/// </para>
/// <para>
/// <see cref="CredentialSchema"/> reports
/// <see cref="AgentRuntimeCredentialKind.None"/>. The probe plan therefore
/// omits the <see cref="UnitValidationStep.ValidatingCredential"/> step —
/// skipping is cleaner than emitting a no-op, and keeps workflow logs
/// accurate about which steps actually ran.
/// </para>
/// <para>
/// <see cref="DefaultModels"/> is loaded once at construction from the
/// runtime's embedded <c>agent-runtimes/ollama/seed.json</c> catalog.
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
    /// refresh-models calls. Registered by the runtime's DI extension;
    /// resolved on each call so a test harness can swap the handler chain
    /// without reconstructing the runtime.
    /// </summary>
    public const string HttpClientName = "Cvoya.Spring.AgentRuntimes.Ollama";

    private static readonly TimeSpan VerifyToolTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ResolveModelTimeout = TimeSpan.FromSeconds(15);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<OllamaAgentRuntimeOptions> _options;
    private readonly ILogger<OllamaAgentRuntime> _logger;
    private readonly Lazy<IReadOnlyList<ModelDescriptor>> _defaultModels;

    /// <summary>
    /// Constructs the runtime with the dependencies provided by DI.
    /// </summary>
    /// <param name="httpClientFactory">Factory used to obtain the named HTTP client for refresh-models.</param>
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
    // Ollama runs without a credential — the tier-2 resolver treats the
    // empty string as "no credential to look up" and returns NotFound
    // without consulting the secret store.
    public string CredentialSecretName => string.Empty;

    /// <inheritdoc />
    public IReadOnlyList<ModelDescriptor> DefaultModels => _defaultModels.Value;

    /// <inheritdoc />
    public IReadOnlyList<ProbeStep> GetProbeSteps(AgentRuntimeInstallConfig config, string credential)
    {
        ArgumentNullException.ThrowIfNull(config);
        // Ollama is credential-less (CredentialSchema.Kind == None). The
        // workflow should pass an empty string here; we ignore whatever
        // we get. We intentionally skip UnitValidationStep.ValidatingCredential
        // rather than emit a no-op so workflow logs only show steps that
        // actually ran.
        _ = credential;

        var baseUrl = string.IsNullOrWhiteSpace(config.BaseUrl)
            ? _options.Value.BaseUrl?.TrimEnd('/') ?? string.Empty
            : config.BaseUrl!.TrimEnd('/');
        var model = config.DefaultModel ?? string.Empty;

        // Probe /api/tags for reachability (VerifyingTool does double-duty:
        // confirms curl is present AND the endpoint is reachable inside
        // the container). The ResolvingModel step filters the same tags
        // payload for the configured model id.
        var tagsCmd = $"curl -sS -o /dev/null -w '%{{http_code}}' '{baseUrl}/api/tags'";
        var resolveModelCmd = $"curl -sS -w '\\n%{{http_code}}' '{baseUrl}/api/tags'";

        return new[]
        {
            new ProbeStep(
                Step: UnitValidationStep.VerifyingTool,
                Args: new[] { "sh", "-c", tagsCmd },
                Env: new Dictionary<string, string>(StringComparer.Ordinal),
                Timeout: VerifyToolTimeout,
                InterpretOutput: InterpretVerifyTool),

            new ProbeStep(
                Step: UnitValidationStep.ResolvingModel,
                Args: new[] { "sh", "-c", resolveModelCmd },
                Env: new Dictionary<string, string>(StringComparer.Ordinal),
                Timeout: ResolveModelTimeout,
                InterpretOutput: (exit, stdout, stderr) => InterpretResolveModel(exit, stdout, stderr, model)),
        };
    }

    internal static StepResult InterpretVerifyTool(int exitCode, string stdout, string stderr)
    {
        // curl exit code non-zero = binary missing or endpoint unreachable;
        // either way the operator has to act, so surface as ToolMissing.
        if (exitCode != 0)
        {
            var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
            return StepResult.Fail(
                UnitValidationCodes.ToolMissing,
                $"`curl` against the Ollama endpoint exited with code {exitCode}. {Trim(detail)}".TrimEnd(),
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["exit_code"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                });
        }

        var statusText = stdout?.Trim() ?? string.Empty;
        if (!int.TryParse(statusText, System.Globalization.CultureInfo.InvariantCulture, out var status))
        {
            return StepResult.Fail(
                UnitValidationCodes.ProbeInternalError,
                $"Could not parse HTTP status from curl output. stdout='{Trim(statusText)}' stderr='{Trim(stderr)}'.");
        }

        if (status is >= 200 and < 300)
        {
            return StepResult.Succeed();
        }
        return StepResult.Fail(
            UnitValidationCodes.ToolMissing,
            $"Ollama endpoint returned HTTP {status} — server unreachable or misconfigured.",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["http_status"] = status.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
    }

    internal static StepResult InterpretResolveModel(int exitCode, string stdout, string stderr, string model)
    {
        if (exitCode != 0)
        {
            return StepResult.Fail(
                UnitValidationCodes.ProbeInternalError,
                $"`curl` against the Ollama tags endpoint exited with code {exitCode}. {Trim(stderr)}".TrimEnd());
        }

        var trimmed = stdout?.TrimEnd() ?? string.Empty;
        var lastNewline = trimmed.LastIndexOf('\n');
        if (lastNewline < 0)
        {
            return StepResult.Fail(
                UnitValidationCodes.ProbeInternalError,
                "Ollama tags response did not include an HTTP status trailer.");
        }
        var body = trimmed[..lastNewline];
        var statusText = trimmed[(lastNewline + 1)..].Trim();

        if (!int.TryParse(statusText, System.Globalization.CultureInfo.InvariantCulture, out var status))
        {
            return StepResult.Fail(
                UnitValidationCodes.ProbeInternalError,
                $"Could not parse HTTP status from curl output. trailer='{statusText}'.");
        }
        if (status is < 200 or >= 300)
        {
            return StepResult.Fail(
                UnitValidationCodes.ProbeInternalError,
                $"Ollama tags endpoint returned HTTP {status}.");
        }

        var names = ParseTagNames(body);
        var csv = string.Join(",", names);
        if (!names.Any(n => string.Equals(n, model, StringComparison.Ordinal)))
        {
            return StepResult.Fail(
                UnitValidationCodes.ModelNotFound,
                $"Model '{model}' was not in the Ollama catalog. Available: {csv}.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["model"] = model,
                    ["models"] = csv,
                });
        }

        return StepResult.Succeed(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["model"] = model,
                ["models"] = csv,
            });
    }

    private static IReadOnlyList<string> ParseTagNames(string body)
    {
        try
        {
            var payload = JsonSerializer.Deserialize(body, OllamaTagsJsonContext.Default.OllamaTagsResponse);
            if (payload?.Models is null)
            {
                return Array.Empty<string>();
            }
            return payload.Models
                .Where(m => !string.IsNullOrWhiteSpace(m.Name))
                .Select(m => m.Name!)
                .ToArray();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static string Trim(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }
        var trimmed = value.Trim();
        const int maxLen = 400;
        return trimmed.Length <= maxLen ? trimmed : trimmed[..maxLen] + "…";
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
}

/// <summary>Subset of Ollama's <c>GET /api/tags</c> envelope we parse during live-model fetch.</summary>
internal sealed record OllamaTagsResponse(
    [property: JsonPropertyName("models")] OllamaTagsModelDto[]? Models);

/// <summary>One entry in the Ollama tags response.</summary>
internal sealed record OllamaTagsModelDto(
    [property: JsonPropertyName("name")] string? Name);

[JsonSerializable(typeof(OllamaTagsResponse))]
internal partial class OllamaTagsJsonContext : JsonSerializerContext;