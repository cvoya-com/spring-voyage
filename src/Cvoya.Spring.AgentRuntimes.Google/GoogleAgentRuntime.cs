// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Google;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="IAgentRuntime"/> for the Google AI (Generative Language) API
/// combined with the in-process <c>dapr-agent</c> execution tool. Updated
/// in T-03 (#945) to produce in-container probe plans for the Dapr
/// <c>UnitValidationWorkflow</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>In-container probes.</b> The runtime image ships <c>curl</c>; probes
/// run against <see cref="DefaultBaseUrl"/> via the same
/// <c>?key=&lt;credential&gt;</c> endpoint the host-side validator used.
/// The host never shells out; the workflow execs curl inside the unit's
/// container.
/// </para>
/// <para>
/// <b>Live model catalog.</b> <see cref="FetchLiveModelsAsync"/> still
/// issues a read-only <c>GET /v1beta/models</c> against
/// <see cref="DefaultBaseUrl"/> via the named HTTP client wired with the
/// credential-health watchdog (CONVENTIONS.md § 16).
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

    // Env var the in-container curl probe reads; kept private so the probe
    // arg payload is the only contract consumers see.
    private const string CredentialEnvVar = "GOOGLE_API_KEY";

    // Probe timeouts — conservative caps for an HTTP round-trip inside the
    // unit container.
    private static readonly TimeSpan VerifyToolTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ValidateCredentialTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ResolveModelTimeout = TimeSpan.FromSeconds(15);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<GoogleAgentRuntime> _logger;
    private readonly Lazy<GoogleAgentRuntimeSeed> _seed;
    private readonly Lazy<IReadOnlyList<ModelDescriptor>> _defaultModels;

    /// <summary>
    /// Creates a runtime that loads its seed from the assembly directory
    /// (the standard production path).
    /// </summary>
    /// <param name="httpClientFactory">Factory for the outbound HTTP client used by refresh-models.</param>
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
    /// <param name="httpClientFactory">Factory for the outbound HTTP client used by refresh-models.</param>
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
    public string CredentialSecretName => "google-api-key";

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
    public IReadOnlyList<ProbeStep> GetProbeSteps(AgentRuntimeInstallConfig config, string credential)
    {
        ArgumentNullException.ThrowIfNull(config);
        credential ??= string.Empty;

        var baseUrl = string.IsNullOrWhiteSpace(config.BaseUrl)
            ? EffectiveBaseUrl
            : config.BaseUrl!.TrimEnd('/');
        var model = config.DefaultModel ?? string.Empty;

        // We pass the credential through env and reference it from the curl
        // command with shell substitution. The in-container probe runs
        // under `sh -c` so the expansion resolves at exec time without
        // embedding the raw key on the argv.
        var credentialEnv = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CredentialEnvVar] = credential,
        };

        // -sS: silent but show errors
        // -o /dev/null: discard body (status-code check only)
        // -w '%{http_code}': print the HTTP status code to stdout
        var validateCmd = $"curl -sS -o /dev/null -w '%{{http_code}}' '{baseUrl}{ValidationPath}?key='\"${CredentialEnvVar}\"";
        var resolveModelCmd = $"curl -sS -w '\\n%{{http_code}}' '{baseUrl}/v1beta/models/{Uri.EscapeDataString(model)}?key='\"${CredentialEnvVar}\"";

        return new[]
        {
            new ProbeStep(
                Step: UnitValidationStep.VerifyingTool,
                Args: new[] { "sh", "-c", "curl --version" },
                Env: new Dictionary<string, string>(StringComparer.Ordinal),
                Timeout: VerifyToolTimeout,
                InterpretOutput: InterpretVerifyTool),

            new ProbeStep(
                Step: UnitValidationStep.ValidatingCredential,
                Args: new[] { "sh", "-c", validateCmd },
                Env: credentialEnv,
                Timeout: ValidateCredentialTimeout,
                InterpretOutput: InterpretValidateCredentialFromHttpStatus),

            new ProbeStep(
                Step: UnitValidationStep.ResolvingModel,
                Args: new[] { "sh", "-c", resolveModelCmd },
                Env: credentialEnv,
                Timeout: ResolveModelTimeout,
                InterpretOutput: (exit, stdout, stderr) => InterpretResolveModel(exit, stdout, stderr, model)),
        };
    }

    internal static StepResult InterpretVerifyTool(int exitCode, string stdout, string stderr)
    {
        if (exitCode == 0)
        {
            return StepResult.Succeed();
        }
        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        return StepResult.Fail(
            UnitValidationCodes.ToolMissing,
            $"`curl --version` exited with code {exitCode}. {Trim(detail)}".TrimEnd(),
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["exit_code"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
    }

    internal static StepResult InterpretValidateCredentialFromHttpStatus(int exitCode, string stdout, string stderr)
    {
        // curl prints the HTTP status code as the only thing on stdout.
        var statusText = stdout?.Trim() ?? string.Empty;
        if (!int.TryParse(statusText, System.Globalization.CultureInfo.InvariantCulture, out var status))
        {
            return StepResult.Fail(
                UnitValidationCodes.ProbeInternalError,
                $"Could not parse HTTP status from curl output (exit {exitCode}). stdout='{Trim(statusText)}' stderr='{Trim(stderr)}'.");
        }

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["http_status"] = status.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };

        return status switch
        {
            >= 200 and < 300 => StepResult.Succeed(),
            401 or 403 => StepResult.Fail(
                UnitValidationCodes.CredentialInvalid,
                $"Google rejected the credential (HTTP {status}).",
                details),
            400 or 422 => StepResult.Fail(
                UnitValidationCodes.CredentialFormatRejected,
                $"Google rejected the credential format (HTTP {status}).",
                details),
            _ => StepResult.Fail(
                UnitValidationCodes.ProbeInternalError,
                $"Google returned HTTP {status}.",
                details),
        };
    }

    internal static StepResult InterpretResolveModel(int exitCode, string stdout, string stderr, string model)
    {
        // Output is <body>\n<status>. We only need the status.
        var trimmed = stdout?.TrimEnd() ?? string.Empty;
        var lastNewline = trimmed.LastIndexOf('\n');
        var statusText = lastNewline >= 0 ? trimmed[(lastNewline + 1)..] : trimmed;
        if (!int.TryParse(statusText.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var status))
        {
            return StepResult.Fail(
                UnitValidationCodes.ProbeInternalError,
                $"Could not parse HTTP status from curl output (exit {exitCode}). stderr='{Trim(stderr)}'.");
        }

        var details = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["http_status"] = status.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["model"] = model,
        };

        return status switch
        {
            >= 200 and < 300 => StepResult.Succeed(
                new Dictionary<string, string>(StringComparer.Ordinal) { ["model"] = model }),
            404 => StepResult.Fail(
                UnitValidationCodes.ModelNotFound,
                $"Model '{model}' was not found (HTTP 404).",
                details),
            401 or 403 => StepResult.Fail(
                UnitValidationCodes.CredentialInvalid,
                $"Google rejected the credential while resolving model (HTTP {status}).",
                details),
            _ => StepResult.Fail(
                UnitValidationCodes.ProbeInternalError,
                $"Google returned HTTP {status} while resolving model '{model}'.",
                details),
        };
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
        if (string.IsNullOrWhiteSpace(credential))
        {
            return FetchLiveModelsResult.InvalidCredential(
                "Supply a Google AI API key to fetch the live model catalog.");
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var uri = $"{EffectiveBaseUrl}{ValidationPath}?key={Uri.EscapeDataString(credential)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                var payload = await response.Content
                    .ReadFromJsonAsync(
                        GoogleModelsJsonContext.Default.GoogleModelsResponse,
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
                    "Google {Path} returned {StatusCode} during live-model fetch; treating as NetworkError. Body: {Body}",
                    ValidationPath, response.StatusCode, body);
                return FetchLiveModelsResult.NetworkError(
                    BuildErrorMessage(response.StatusCode, body, transientPrefix: true));
            }

            // Other 4xx — likely a key scoping problem that the operator
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
                "Network error contacting Google {Path} during live-model fetch.", ValidationPath);
            return FetchLiveModelsResult.NetworkError(
                $"Could not reach the Google AI API: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex,
                "Timeout contacting Google {Path} during live-model fetch.", ValidationPath);
            return FetchLiveModelsResult.NetworkError(
                "Timed out contacting the Google AI API.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse Google {Path} response.", ValidationPath);
            return FetchLiveModelsResult.NetworkError(
                "The Google AI API returned an unexpected response body.");
        }
    }

    private static IReadOnlyList<ModelDescriptor> BuildModels(GoogleModelsResponse? payload)
    {
        if (payload?.Models is null || payload.Models.Length == 0)
        {
            return Array.Empty<ModelDescriptor>();
        }

        var result = new List<ModelDescriptor>(payload.Models.Length);
        foreach (var entry in payload.Models)
        {
            // Google's model Name is prefixed with "models/" (e.g.
            // "models/gemini-2.5-pro") — strip the prefix so the id
            // projection matches the seed catalog shape the rest of the
            // platform uses.
            var id = NormaliseModelId(entry.Name);
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }
            var display = string.IsNullOrWhiteSpace(entry.DisplayName) ? id : entry.DisplayName!;
            result.Add(new ModelDescriptor(id!, display, ContextWindow: entry.InputTokenLimit));
        }
        return result;
    }

    private static string? NormaliseModelId(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        const string prefix = "models/";
        return name.StartsWith(prefix, StringComparison.Ordinal)
            ? name[prefix.Length..]
            : name;
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

/// <summary>Subset of Google's <c>GET /v1beta/models</c> envelope we parse during live-model fetch.</summary>
internal sealed record GoogleModelsResponse(
    [property: JsonPropertyName("models")] GoogleModelDto[]? Models);

/// <summary>One entry in the Google models envelope.</summary>
internal sealed record GoogleModelDto(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("inputTokenLimit")] int? InputTokenLimit);

[JsonSerializable(typeof(GoogleModelsResponse))]
internal partial class GoogleModelsJsonContext : JsonSerializerContext;