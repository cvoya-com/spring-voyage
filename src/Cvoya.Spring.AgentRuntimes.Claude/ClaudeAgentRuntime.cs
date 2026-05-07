// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Claude;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.AgentRuntimes.Claude.Internal;
using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="IAgentRuntime"/> for Anthropic's Claude (Claude Code CLI +
/// Anthropic Platform API). Implements the V2 backend-validation probe
/// contract introduced in T-03 (#945): every runtime-level check runs
/// inside the unit's chosen container image, invoked by the Dapr
/// <c>UnitValidationWorkflow</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>In-container probes.</b>
/// <see cref="GetProbeSteps(AgentRuntimeInstallConfig, string)"/> returns
/// three probes: <c>claude --version</c> (tool presence),
/// <c>claude --bare -p --output-format json &lt;canary&gt;</c>
/// (credential validation via the CLI's JSON result envelope), and a
/// model-resolution step that reuses the credential probe's live catalog
/// hints (Claude's CLI does not expose a models subcommand, so we lean on
/// the same canary call and check the configured model id against the
/// seed + live catalog). Image-pull is dispatcher-owned and never appears
/// here.
/// </para>
/// <para>
/// <b>Live model catalog.</b>
/// <see cref="FetchLiveModelsAsync"/> still hits the Anthropic REST
/// <c>GET /v1/models</c> endpoint directly — the refresh-models path is
/// a host-side, tenant-scoped operation separate from per-unit validation.
/// The named HTTP client remains wired with the credential-health watchdog
/// per CONVENTIONS.md § 16.
/// </para>
/// <para>
/// <b>Default models.</b> Loaded once at construction from the embedded
/// <c>agent-runtimes/claude/seed.json</c> resource. Tenants override or
/// extend the catalog through per-tenant install configuration.
/// </para>
/// </remarks>
public class ClaudeAgentRuntime : IAgentRuntime
{
    /// <summary>Stable runtime identifier — persisted in tenant installs and unit bindings.</summary>
    public const string RuntimeId = "claude";

    /// <summary>Execution-tool identifier shared with any future Claude-backed runtime variant.</summary>
    public const string KindId = "claude-code-cli";

    /// <summary>
    /// Default container image the portal wizard pre-fills when the operator
    /// selects this runtime. Ships the Claude Code CLI pre-installed.
    /// </summary>
    public const string DefaultContainerImage = "ghcr.io/cvoya-com/spring-voyage-agent-claude-code:latest";

    /// <summary>Human-facing display label for UI / CLI surfaces.</summary>
    public const string DisplayLabel = "Claude (Claude Code CLI + Anthropic API)";

    /// <summary>Named <see cref="HttpClient"/> the runtime resolves for the live-catalog refresh path.</summary>
    public const string HttpClientName = "Cvoya.Spring.AgentRuntimes.Claude";

    private const string AnthropicVersion = "2023-06-01";

    /// <summary>Credential prefix that identifies a Claude.ai OAuth token (<c>claude setup-token</c>).</summary>
    private const string OAuthTokenPrefix = "sk-ant-oat";

    /// <summary>Credential prefix that identifies an Anthropic Platform API key (<c>console.anthropic.com</c>).</summary>
    private const string ApiKeyPrefix = "sk-ant-api";

    // Probe timeouts — generous caps so a stuck CLI / network round-trip
    // cannot stall the UnitValidationWorkflow indefinitely. The tool
    // probe is a cheap --version; the credential probe spawns a real
    // bare-mode CLI invocation, so it gets the larger budget. The
    // model-resolution probe reuses the credential canary's output, so
    // it runs a cheap no-op inside the container.
    private static readonly TimeSpan VerifyToolTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ValidateCredentialTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ResolveModelTimeout = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ClaudeAgentRuntime> _logger;
    private readonly ClaudeRuntimeSeed _seed;
    private readonly IReadOnlyList<ModelDescriptor> _defaultModels;

    /// <summary>Production constructor.</summary>
    /// <param name="httpClientFactory">Factory for the named REST-fallback HTTP client.</param>
    /// <param name="logger">Logger.</param>
    public ClaudeAgentRuntime(
        IHttpClientFactory httpClientFactory,
        ILogger<ClaudeAgentRuntime> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _seed = ClaudeRuntimeSeedLoader.Load();
        _defaultModels = BuildDefaultModels(_seed);
    }

    /// <inheritdoc />
    public string Id => RuntimeId;

    /// <inheritdoc />
    public string DisplayName => DisplayLabel;

    /// <inheritdoc />
    public string Kind => KindId;

    /// <inheritdoc />
    public AgentRuntimeCredentialSchema CredentialSchema { get; } = new(
        AgentRuntimeCredentialKind.ApiKey,
        DisplayHint: "Anthropic API key (sk-ant-api…) or Claude.ai OAuth token (sk-ant-oat…) from `claude setup-token`.");

    /// <inheritdoc />
    // Secret is branded after the Anthropic Platform (the key-issuing
    // authority) rather than the runtime id to match the tenant-defaults
    // portal labels and `docs/guide/secrets.md`.
    public string CredentialSecretName => "anthropic-api-key";

    /// <inheritdoc />
    /// <remarks>
    /// The Claude agent runtime is OAuth-only: the <c>claude</c> CLI dispatch
    /// path accepts only Claude.ai OAuth tokens (<c>sk-ant-oat…</c>) and
    /// reads them from <c>CLAUDE_CODE_OAUTH_TOKEN</c>. API keys are usable
    /// on this project only via the Spring Voyage REST dispatch path
    /// (which emits <c>ANTHROPIC_API_KEY</c> from its own launcher).
    /// </remarks>
    public string CredentialEnvVar => "CLAUDE_CODE_OAUTH_TOKEN";

    /// <inheritdoc />
    public IReadOnlyList<ModelDescriptor> DefaultModels => _defaultModels;

    /// <inheritdoc />
    public string DefaultImage => DefaultContainerImage;

    /// <summary>
    /// Default base URL for the Anthropic Platform REST API. Read from
    /// the seed file's <c>baseUrl</c> field; exposed so wrappers
    /// (e.g. a tenant-scoped variant in the private cloud host) can
    /// consult the canonical default.
    /// </summary>
    public string? DefaultBaseUrl => _seed.BaseUrl;

    /// <inheritdoc />
    public IReadOnlyList<ProbeStep> GetProbeSteps(AgentRuntimeInstallConfig config, string credential)
    {
        ArgumentNullException.ThrowIfNull(config);
        credential ??= string.Empty;

        // The Claude agent runtime is OAuth-only (#1714). The pre-flight
        // IsCredentialFormatAccepted check rejects API keys before the
        // probe is ever scheduled, so we always emit the OAuth env var
        // and never run `claude --bare` (which is not used by this
        // project — see CredentialEnvVar's remarks).
        var credentialEnv = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["CLAUDE_CODE_OAUTH_TOKEN"] = credential,
        };

        var model = config.DefaultModel ?? string.Empty;

        // No `--bare`: the project keeps the full Claude CLI surface
        // (hooks, plugins, skills, keychain, auto-memory). The CLI's
        // standard auth path reads CLAUDE_CODE_OAUTH_TOKEN.
        var validateArgs = new[] { "claude", "-p", "--output-format", "json", "respond with OK" };
        var resolveModelArgs = new[] { "claude", "-p", "--output-format", "json", $"--model={model}", "respond with OK" };

        return new[]
        {
            new ProbeStep(
                Step: UnitValidationStep.VerifyingTool,
                Args: new[] { "claude", "--version" },
                Env: new Dictionary<string, string>(StringComparer.Ordinal),
                Timeout: VerifyToolTimeout,
                InterpretOutput: InterpretVerifyTool),

            new ProbeStep(
                Step: UnitValidationStep.ValidatingCredential,
                // The CLI returns a --output-format=json envelope with an
                // `is_error` flag and `api_error_status` for 401 / 403 /
                // 5xx. See validateArgs above for the bare-vs-oauth split.
                Args: validateArgs,
                Env: credentialEnv,
                Timeout: ValidateCredentialTimeout,
                InterpretOutput: InterpretValidateCredential),

            new ProbeStep(
                Step: UnitValidationStep.ResolvingModel,
                // The claude CLI has no models subcommand; we reuse the
                // canary call (credentialed) and read the response
                // envelope's `model` field to confirm the configured model
                // is honoured. Timeout is the credential budget since
                // we're making the same call shape. resolveModelArgs
                // applies the same bare-vs-oauth split as validateArgs.
                Args: resolveModelArgs,
                Env: credentialEnv,
                Timeout: ResolveModelTimeout,
                InterpretOutput: (exit, stdout, stderr) => InterpretResolveModel(exit, stdout, stderr, model)),
        };
    }

    private static StepResult InterpretVerifyTool(int exitCode, string stdout, string stderr)
    {
        if (exitCode == 0)
        {
            return StepResult.Succeed();
        }

        var detail = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
        return StepResult.Fail(
            UnitValidationCodes.ToolMissing,
            message: $"`claude --version` exited with code {exitCode}. {Trim(detail)}".TrimEnd(),
            details: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["exit_code"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
    }

    private static StepResult InterpretValidateCredential(int exitCode, string stdout, string stderr)
    {
        var parsed = TryParseCliResult(stdout);

        if (parsed is null)
        {
            if (exitCode != 0)
            {
                return StepResult.Fail(
                    UnitValidationCodes.ProbeInternalError,
                    $"`claude` exited with code {exitCode} and produced no parseable result. {Trim(stderr)}".TrimEnd());
            }
            return StepResult.Fail(
                UnitValidationCodes.ProbeInternalError,
                "`claude` returned an empty or non-JSON response on stdout.");
        }

        if (parsed.IsError == true)
        {
            return parsed.ApiErrorStatus switch
            {
                401 or 403 => StepResult.Fail(
                    UnitValidationCodes.CredentialInvalid,
                    string.IsNullOrWhiteSpace(parsed.Result)
                        ? "Anthropic rejected the credential."
                        : $"Anthropic rejected the credential: {Trim(parsed.Result!)}",
                    Details(parsed.ApiErrorStatus)),
                400 or 422 => StepResult.Fail(
                    UnitValidationCodes.CredentialFormatRejected,
                    string.IsNullOrWhiteSpace(parsed.Result)
                        ? $"Anthropic rejected the credential format (HTTP {parsed.ApiErrorStatus})."
                        : $"Anthropic rejected the credential format: {Trim(parsed.Result!)}",
                    Details(parsed.ApiErrorStatus)),
                _ => StepResult.Fail(
                    UnitValidationCodes.ProbeInternalError,
                    $"Anthropic returned an error (HTTP {parsed.ApiErrorStatus?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?"}): {Trim(parsed.Result ?? string.Empty)}".TrimEnd(),
                    Details(parsed.ApiErrorStatus)),
            };
        }

        return StepResult.Succeed();
    }

    private static StepResult InterpretResolveModel(int exitCode, string stdout, string stderr, string model)
    {
        var parsed = TryParseCliResult(stdout);
        if (parsed is null)
        {
            if (exitCode != 0)
            {
                return StepResult.Fail(
                    UnitValidationCodes.ProbeInternalError,
                    $"`claude` exited with code {exitCode} while resolving model. {Trim(stderr)}".TrimEnd());
            }
            return StepResult.Fail(
                UnitValidationCodes.ProbeInternalError,
                "`claude` returned an empty or non-JSON response on stdout while resolving model.");
        }

        if (parsed.IsError == true)
        {
            // 404 / "model not found" envelopes come back with the generic
            // error shape; map them to ModelNotFound. Other errors propagate
            // as ProbeInternalError — the credential step already owns
            // 401/403 classification.
            return StepResult.Fail(
                UnitValidationCodes.ModelNotFound,
                string.IsNullOrWhiteSpace(parsed.Result)
                    ? $"Model '{model}' was rejected by Anthropic."
                    : $"Model '{model}' was rejected by Anthropic: {Trim(parsed.Result!)}",
                new Dictionary<string, string>(StringComparer.Ordinal) { ["model"] = model });
        }

        return StepResult.Succeed(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["model"] = model,
            });
    }

    private static ClaudeCliResult? TryParseCliResult(string stdout)
    {
        var trimmed = stdout?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(trimmed, ClaudeCliJsonContext.Default.ClaudeCliResult);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string>? Details(int? status) =>
        status is null
            ? null
            : new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["http_status"] = status.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };

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
                "Supply an Anthropic API key (sk-ant-api…) to fetch the live model catalog.");
        }

        // The Anthropic Platform REST endpoint rejects Claude.ai OAuth
        // tokens with a 401 indistinguishable from a bad key. The CLI
        // does not expose a models subcommand, so we cannot fulfil the
        // fetch for OAuth credentials — surface it as Unsupported so
        // operators get a precise message instead of a misleading
        // "invalid credential" flip.
        if (credential.StartsWith(OAuthTokenPrefix, StringComparison.Ordinal))
        {
            return FetchLiveModelsResult.Unsupported(
                "Claude.ai OAuth tokens (from `claude setup-token`) cannot enumerate models through the Anthropic Platform REST API. " +
                "Supply an Anthropic API key (sk-ant-api…) to refresh, or keep the seed catalog.");
        }

        var baseUrl = string.IsNullOrWhiteSpace(_seed.BaseUrl)
            ? "https://api.anthropic.com"
            : _seed.BaseUrl!.TrimEnd('/');

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/models");
        request.Headers.Add("x-api-key", credential);
        request.Headers.Add("anthropic-version", AnthropicVersion);

        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return FetchLiveModelsResult.InvalidCredential(
                    $"Anthropic rejected the key (HTTP {(int)response.StatusCode}). " +
                    "Check that it is a live API key with models access.");
            }
            if (!response.IsSuccessStatusCode)
            {
                return FetchLiveModelsResult.NetworkError(
                    $"Anthropic responded with HTTP {(int)response.StatusCode} {response.StatusCode}.");
            }

            var payload = await response.Content
                .ReadFromJsonAsync(AnthropicRestJsonContext.Default.AnthropicModelsResponse, cancellationToken)
                .ConfigureAwait(false);

            var models = BuildModels(payload);
            return FetchLiveModelsResult.Success(models);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Network error fetching Anthropic live model list.");
            return FetchLiveModelsResult.NetworkError(
                $"Could not reach the Anthropic API: {ex.Message}");
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Timeout fetching Anthropic live model list.");
            return FetchLiveModelsResult.NetworkError(
                "Timed out contacting the Anthropic API.");
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse Anthropic live-models response.");
            return FetchLiveModelsResult.NetworkError(
                "The Anthropic API returned an unexpected response body.");
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>
    /// The Claude agent runtime is OAuth-only (#1714). Each dispatch path
    /// accepts exactly one credential shape, with no shape-branching at
    /// the launcher:
    /// </para>
    /// <list type="bullet">
    ///   <item>
    ///     <see cref="CredentialDispatchPath.AgentRuntime"/> — the
    ///     <c>claude</c> CLI inside a unit container (NOT
    ///     <c>--bare</c>). Accepts only Claude.ai OAuth tokens
    ///     (<c>sk-ant-oat…</c>) generated by <c>claude setup-token</c>.
    ///     Injected as <c>CLAUDE_CODE_OAUTH_TOKEN</c>. API keys
    ///     (<c>sk-ant-api…</c>) are rejected pre-flight; the project
    ///     does not run <c>claude --bare</c>, so the full Claude CLI
    ///     surface (hooks, plugins, skills, keychain, auto-memory) is
    ///     always available.
    ///   </item>
    ///   <item>
    ///     <see cref="CredentialDispatchPath.Rest"/> — the Anthropic
    ///     Messages API used by the Spring Voyage runtime via Dapr
    ///     Conversation. Accepts only Anthropic Platform API keys
    ///     (<c>sk-ant-api…</c>); OAuth tokens are rejected with a 401
    ///     by the Anthropic endpoint and surfaced as
    ///     <c>format-rejected</c> here so the wizard never shows a
    ///     green badge for a token the path will reject (#981, #1003).
    ///   </item>
    /// </list>
    /// <para>
    /// Values that are neither shape (no <c>sk-ant-api…</c> nor
    /// <c>sk-ant-oat…</c> prefix) are rejected on both paths.
    /// </para>
    /// </remarks>
    public bool IsCredentialFormatAccepted(string credential, CredentialDispatchPath dispatchPath)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            // Empty is "not configured" — the resolver owns that state;
            // pre-flight format check is only concerned with real values.
            return true;
        }

        var isApiKey = credential.StartsWith(ApiKeyPrefix, StringComparison.Ordinal);
        var isOAuth = credential.StartsWith(OAuthTokenPrefix, StringComparison.Ordinal);

        // Neither-shape: Anthropic accepts no other prefix on either
        // surface. Reject pre-flight on every path so the wizard surfaces
        // `format-rejected` instead of a misleading 401 from the live
        // probe.
        if (!isApiKey && !isOAuth)
        {
            return false;
        }

        // Strict per-path acceptance (#1714): the Claude agent-runtime
        // path is OAuth-only; the Spring Voyage REST path is API-key-only.
        // No shape branching at the launcher.
        return dispatchPath switch
        {
            // OAuth tokens only — the project never runs `claude --bare`.
            CredentialDispatchPath.AgentRuntime => isOAuth,
            // API keys only — Anthropic Platform REST rejects OAuth.
            CredentialDispatchPath.Rest => isApiKey,
            _ => false,
        };
    }

    private static IReadOnlyList<ModelDescriptor> BuildModels(AnthropicModelsResponse? payload)
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
            var display = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Id! : entry.DisplayName!;
            result.Add(new ModelDescriptor(entry.Id!, display, ContextWindow: null));
        }
        return result;
    }

    private static IReadOnlyList<ModelDescriptor> BuildDefaultModels(ClaudeRuntimeSeed seed)
    {
        var list = new List<ModelDescriptor>(seed.Models.Count);
        foreach (var id in seed.Models)
        {
            list.Add(DescribeSeedModel(id));
        }
        return list;
    }

    /// <summary>
    /// Maps seed ids to UI labels and approximate context limits. Unknown
    /// ids fall back to the wire id as the display name (same as the REST
    /// catalog path). Claude Code may list two Sonnet 4.6 presets (standard
    /// vs 1M billing); both resolve to the same Anthropic model id
    /// <c>claude-sonnet-4-6</c>.
    /// </summary>
    private static ModelDescriptor DescribeSeedModel(string id) =>
        id switch
        {
            "claude-opus-4-7" => new ModelDescriptor(
                id,
                "Claude Opus 4.7 (1M context)",
                ContextWindow: 1_000_000),
            "claude-sonnet-4-6" => new ModelDescriptor(
                id,
                "Claude Sonnet 4.6",
                ContextWindow: 1_000_000),
            "claude-haiku-4-5" => new ModelDescriptor(
                id,
                "Claude Haiku 4.5",
                ContextWindow: 200_000),
            _ => new ModelDescriptor(id, id, ContextWindow: null),
        };
}

/// <summary>Subset of Anthropic's <c>GET /v1/models</c> response we need to drain after a successful REST validation.</summary>
internal sealed record AnthropicModelsResponse(
    [property: JsonPropertyName("data")] AnthropicModelDto[]? Data);

/// <summary>One entry in the Anthropic models response.</summary>
internal sealed record AnthropicModelDto(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("display_name")] string? DisplayName);

[JsonSerializable(typeof(AnthropicModelsResponse))]
internal partial class AnthropicRestJsonContext : JsonSerializerContext;

/// <summary>
/// Subset of the <c>claude --output-format json</c> response that the
/// credential / model-resolution probes parse. Lives next to its caller so
/// the in-container probe interpreter stays in-process. Other fields the
/// envelope carries are ignored.
/// </summary>
internal sealed record ClaudeCliResult(
    [property: JsonPropertyName("type")] string? Type,
    [property: JsonPropertyName("subtype")] string? Subtype,
    [property: JsonPropertyName("is_error")] bool? IsError,
    [property: JsonPropertyName("api_error_status")] int? ApiErrorStatus,
    [property: JsonPropertyName("result")] string? Result);

[JsonSerializable(typeof(ClaudeCliResult))]
internal partial class ClaudeCliJsonContext : JsonSerializerContext;