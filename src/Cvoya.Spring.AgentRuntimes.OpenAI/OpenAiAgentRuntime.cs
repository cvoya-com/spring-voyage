// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.OpenAI;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="IAgentRuntime"/> for the OpenAI Platform API combined with
/// the in-process <c>dapr-agent</c> execution tool. Updated in T-03 (#945)
/// to produce in-container probe plans for the Dapr
/// <c>UnitValidationWorkflow</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>In-container probes.</b> The runtime image ships <c>curl</c>; probes
/// issue <c>curl -H 'Authorization: Bearer …'</c> against
/// <see cref="DefaultBaseUrl"/>. The host never shells out.
/// </para>
/// <para>
/// <b>Live model catalog.</b> <see cref="FetchLiveModelsAsync"/> still
/// uses the named HTTP client (wired with the credential-health watchdog
/// per CONVENTIONS.md § 16) for host-side refresh-models calls.
/// </para>
/// </remarks>
public class OpenAiAgentRuntime : IAgentRuntime
{
    /// <summary>The named <see cref="HttpClient"/> the runtime resolves from <see cref="IHttpClientFactory"/>.</summary>
    public const string HttpClientName = "Cvoya.Spring.AgentRuntimes.OpenAI";

    /// <summary>The OpenAI Platform API base URL used when the seed does not pin a value.</summary>
    public const string DefaultBaseUrl = "https://api.openai.com";

    private const string CredentialEnvVar = "OPENAI_API_KEY";

    // Probe timeouts — conservative caps for an HTTP round-trip inside the
    // unit container.
    private static readonly TimeSpan VerifyToolTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ValidateCredentialTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ResolveModelTimeout = TimeSpan.FromSeconds(15);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<OpenAiAgentRuntime> _logger;
    private readonly Lazy<OpenAiAgentRuntimeSeed> _seed;
    private readonly Lazy<IReadOnlyList<ModelDescriptor>> _defaultModels;

    /// <summary>
    /// Creates a runtime that loads its seed from the assembly directory
    /// (the standard production path).
    /// </summary>
    /// <param name="httpClientFactory">Factory for the outbound HTTP client used by refresh-models.</param>
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
    public string CredentialSecretName => "openai-api-key";

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

        var credentialEnv = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CredentialEnvVar] = credential,
        };

        var validateCmd = $"curl -sS -o /dev/null -w '%{{http_code}}' -H \"Authorization: Bearer ${CredentialEnvVar}\" '{baseUrl}/v1/models'";
        var resolveModelCmd = $"curl -sS -w '\\n%{{http_code}}' -H \"Authorization: Bearer ${CredentialEnvVar}\" '{baseUrl}/v1/models/{Uri.EscapeDataString(model)}'";

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
                $"OpenAI rejected the credential (HTTP {status}).",
                details),
            400 or 422 => StepResult.Fail(
                UnitValidationCodes.CredentialFormatRejected,
                $"OpenAI rejected the credential format (HTTP {status}).",
                details),
            _ => StepResult.Fail(
                UnitValidationCodes.ProbeInternalError,
                $"OpenAI returned HTTP {status}.",
                details),
        };
    }

    internal static StepResult InterpretResolveModel(int exitCode, string stdout, string stderr, string model)
    {
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
                $"OpenAI rejected the credential while resolving model (HTTP {status}).",
                details),
            _ => StepResult.Fail(
                UnitValidationCodes.ProbeInternalError,
                $"OpenAI returned HTTP {status} while resolving model '{model}'.",
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
    /// <remarks>
    /// OpenAI does not issue distinct credential prefixes that need
    /// per-path gating. Both dispatch paths (REST host-side completions
    /// and the in-container <c>dapr-agent</c> runtime) accept whatever
    /// API key shape OpenAI issues; invalid values surface at the
    /// network layer rather than as a pre-flight format rejection.
    /// </remarks>
    public bool IsCredentialFormatAccepted(string credential, CredentialDispatchPath dispatchPath) => true;

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