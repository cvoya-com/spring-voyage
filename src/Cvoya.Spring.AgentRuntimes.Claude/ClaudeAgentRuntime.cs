// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentRuntimes.Claude;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.AgentRuntimes.Claude.Internal;
using Cvoya.Spring.Core.AgentRuntimes;

using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="IAgentRuntime"/> for Anthropic's Claude (Claude Code CLI +
/// Anthropic Platform API). Implements the plugin contract introduced in
/// #678 and folded together with the container-baseline migration #668
/// under #679.
/// </summary>
/// <remarks>
/// <para>
/// <b>Credential validation.</b> Two credential formats reach this
/// runtime: Anthropic Platform API keys (<c>sk-ant-api…</c>) and
/// Claude.ai OAuth tokens (<c>sk-ant-oat…</c>). Both are validated by
/// shelling out to the <c>claude</c> CLI bundled in the runtime's
/// container image — the CLI handles both formats transparently. API
/// keys also fall back to a direct REST <c>GET /v1/models</c> call when
/// the CLI is unavailable, replicating the host-CLI-optional behavior of
/// the legacy <c>ProviderCredentialValidator</c>. OAuth tokens never
/// reach REST: the Anthropic Platform endpoint rejects them with a 401
/// indistinguishable from a bad key, so we surface a precise "CLI
/// unavailable" error instead.
/// </para>
/// <para>
/// <b>Container baseline.</b> <see cref="VerifyContainerBaselineAsync"/>
/// runs <c>claude --version</c> in the runtime's container. The wizard
/// and install flow consult it before letting tenants enable the runtime;
/// a host that lacks the CLI gets a clear error at install time instead
/// of cryptic credential failures at unit-run time. This closes #668.
/// </para>
/// <para>
/// <b>Default models.</b> Loaded once at construction from the embedded
/// <c>agent-runtimes/claude/seed.json</c> resource, mirroring the
/// curated list previously hard-coded in
/// <c>ModelCatalog.StaticFallback["claude"]</c>. Tenants override or
/// extend the catalog through per-tenant install configuration (Phase 3
/// — the wizard rework lands separately under #690).
/// </para>
/// </remarks>
public class ClaudeAgentRuntime : IAgentRuntime
{
    /// <summary>Stable runtime identifier — persisted in tenant installs and unit bindings.</summary>
    public const string RuntimeId = "claude";

    /// <summary>Execution-tool identifier shared with any future Claude-backed runtime variant.</summary>
    public const string ToolKindId = "claude-code-cli";

    /// <summary>Human-facing display label for UI / CLI surfaces.</summary>
    public const string DisplayLabel = "Claude (Claude Code CLI + Anthropic API)";

    /// <summary>Named <see cref="HttpClient"/> the runtime resolves for the REST fallback path.</summary>
    public const string HttpClientName = "Cvoya.Spring.AgentRuntimes.Claude";

    private const string AnthropicVersion = "2023-06-01";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ClaudeAgentRuntime> _logger;
    private readonly ClaudeCliInvoker _cli;
    private readonly ClaudeRuntimeSeed _seed;
    private readonly IReadOnlyList<ModelDescriptor> _defaultModels;

    /// <summary>Production constructor — builds a CLI invoker over <see cref="DefaultProcessRunner"/>.</summary>
    /// <param name="httpClientFactory">Factory for the named REST-fallback HTTP client.</param>
    /// <param name="logger">Logger.</param>
    public ClaudeAgentRuntime(
        IHttpClientFactory httpClientFactory,
        ILogger<ClaudeAgentRuntime> logger)
        : this(httpClientFactory, DefaultProcessRunner.Instance, logger)
    {
    }

    /// <summary>
    /// Test-friendly constructor that lets callers inject a stub
    /// <see cref="IProcessRunner"/>. Internal because the process-runner
    /// abstraction is a private detail of this project; the parameterless
    /// overload is the supported public seam.
    /// </summary>
    internal ClaudeAgentRuntime(
        IHttpClientFactory httpClientFactory,
        IProcessRunner processRunner,
        ILogger<ClaudeAgentRuntime> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(processRunner);
        ArgumentNullException.ThrowIfNull(logger);

        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _cli = new ClaudeCliInvoker(processRunner, logger);
        _seed = ClaudeRuntimeSeedLoader.Load();
        _defaultModels = BuildDefaultModels(_seed);
    }

    /// <inheritdoc />
    public string Id => RuntimeId;

    /// <inheritdoc />
    public string DisplayName => DisplayLabel;

    /// <inheritdoc />
    public string ToolKind => ToolKindId;

    /// <inheritdoc />
    public AgentRuntimeCredentialSchema CredentialSchema { get; } = new(
        AgentRuntimeCredentialKind.ApiKey,
        DisplayHint: "Anthropic API key (sk-ant-api…) or Claude.ai OAuth token (sk-ant-oat…) from `claude setup-token`.");

    /// <inheritdoc />
    public IReadOnlyList<ModelDescriptor> DefaultModels => _defaultModels;

    /// <summary>
    /// Default base URL for the Anthropic Platform REST API. Read from
    /// the seed file's <c>baseUrl</c> field; exposed so wrappers
    /// (e.g. a tenant-scoped variant in the private cloud host) can
    /// consult the canonical default.
    /// </summary>
    public string? DefaultBaseUrl => _seed.BaseUrl;

    /// <inheritdoc />
    public async Task<CredentialValidationResult> ValidateCredentialAsync(
        string credential,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(credential))
        {
            return new CredentialValidationResult(
                false,
                ErrorMessage: "Supply an Anthropic API key (sk-ant-api…) or Claude.ai token (sk-ant-oat…) to validate.",
                Status: CredentialValidationStatus.Invalid);
        }

        var isOAuth = ClaudeCliInvoker.IsOAuthToken(credential);

        try
        {
            // 1) Try the CLI first when present. The CLI accepts both
            // OAuth tokens and API keys, so successful validation tells
            // us the credential is live regardless of format.
            var cliBaseline = await _cli.ProbeBaselineAsync(cancellationToken).ConfigureAwait(false);
            if (cliBaseline.Passed)
            {
                var cliResult = await _cli.ValidateAsync(credential, cancellationToken).ConfigureAwait(false);
                return MapCliResult(cliResult);
            }

            // 2) The CLI is unavailable. OAuth tokens cannot be
            //    validated through the REST endpoint — Anthropic
            //    rejects them with a 401 indistinguishable from a bad
            //    key — so stop here with a precise error message.
            if (isOAuth)
            {
                return new CredentialValidationResult(
                    false,
                    ErrorMessage: "Claude.ai tokens (from `claude setup-token`) require the claude CLI in the runtime container to validate. " +
                        "Confirm VerifyContainerBaselineAsync passes, or supply an Anthropic API key (sk-ant-api…) instead.",
                    Status: CredentialValidationStatus.Invalid);
            }

            // 3) API key without CLI — fall back to a REST probe.
            return await ValidateApiKeyViaRestAsync(credential, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Network error validating Anthropic credential.");
            return new CredentialValidationResult(
                false,
                ErrorMessage: $"Could not reach the Anthropic API: {ex.Message}",
                Status: CredentialValidationStatus.NetworkError);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Timeout validating Anthropic credential.");
            return new CredentialValidationResult(
                false,
                ErrorMessage: "Timed out contacting the Anthropic API.",
                Status: CredentialValidationStatus.NetworkError);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse Anthropic validation response.");
            return new CredentialValidationResult(
                false,
                ErrorMessage: "The Anthropic API returned an unexpected response body.",
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
                "Supply an Anthropic API key (sk-ant-api…) to fetch the live model catalog.");
        }

        // The Anthropic Platform REST endpoint rejects Claude.ai OAuth
        // tokens with a 401 indistinguishable from a bad key. The CLI
        // does not expose a models subcommand, so we cannot fulfil the
        // fetch for OAuth credentials — surface it as Unsupported so
        // operators get a precise message instead of a misleading
        // "invalid credential" flip.
        if (ClaudeCliInvoker.IsOAuthToken(credential))
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
            // Anthropic's /v1/models envelope doesn't publish a context
            // window — DisplayName falls back to Id to match the seed
            // catalog projection.
            var display = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Id! : entry.DisplayName!;
            result.Add(new ModelDescriptor(entry.Id!, display, ContextWindow: null));
        }
        return result;
    }

    /// <inheritdoc />
    public async Task<ContainerBaselineCheckResult> VerifyContainerBaselineAsync(
        CancellationToken cancellationToken = default)
    {
        var probe = await _cli.ProbeBaselineAsync(cancellationToken).ConfigureAwait(false);
        if (probe.Passed)
        {
            return new ContainerBaselineCheckResult(true, Array.Empty<string>());
        }

        return new ContainerBaselineCheckResult(
            false,
            new[] { probe.ErrorMessage ?? $"`{ClaudeCliInvoker.DefaultExecutable} --version` failed." });
    }

    private async Task<CredentialValidationResult> ValidateApiKeyViaRestAsync(
        string apiKey,
        CancellationToken cancellationToken)
    {
        var baseUrl = string.IsNullOrWhiteSpace(_seed.BaseUrl)
            ? "https://api.anthropic.com"
            : _seed.BaseUrl!.TrimEnd('/');

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/v1/models");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", AnthropicVersion);

        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return new CredentialValidationResult(
                false,
                ErrorMessage: $"Anthropic rejected the key (HTTP {(int)response.StatusCode}). " +
                    "Check that it is a live API key with models access.",
                Status: CredentialValidationStatus.Invalid);
        }
        if (!response.IsSuccessStatusCode)
        {
            return new CredentialValidationResult(
                false,
                ErrorMessage: $"Anthropic responded with HTTP {(int)response.StatusCode} {response.StatusCode}.",
                Status: CredentialValidationStatus.NetworkError);
        }

        // Drain the body so the connection returns to the pool cleanly,
        // even though we don't currently surface the live model list
        // through the IAgentRuntime contract (DefaultModels is the seed
        // catalog; per-credential enrichment is wizard-side concern that
        // arrives with the Phase 3 wizard rework, #690).
        _ = await response.Content
            .ReadFromJsonAsync(AnthropicRestJsonContext.Default.AnthropicModelsResponse, cancellationToken)
            .ConfigureAwait(false);

        return new CredentialValidationResult(true, ErrorMessage: null, Status: CredentialValidationStatus.Valid);
    }

    private static CredentialValidationResult MapCliResult(ClaudeCliValidationResult cli) =>
        cli.Outcome switch
        {
            ClaudeCliValidationOutcome.Valid => new CredentialValidationResult(
                true, ErrorMessage: null, Status: CredentialValidationStatus.Valid),
            ClaudeCliValidationOutcome.Unauthorized => new CredentialValidationResult(
                false, cli.ErrorMessage, CredentialValidationStatus.Invalid),
            ClaudeCliValidationOutcome.NetworkError => new CredentialValidationResult(
                false, cli.ErrorMessage, CredentialValidationStatus.NetworkError),
            ClaudeCliValidationOutcome.MissingCredential => new CredentialValidationResult(
                false, cli.ErrorMessage, CredentialValidationStatus.Invalid),
            ClaudeCliValidationOutcome.CliMissing => new CredentialValidationResult(
                false, cli.ErrorMessage, CredentialValidationStatus.NetworkError),
            _ => new CredentialValidationResult(
                false, cli.ErrorMessage ?? "The claude CLI failed.", CredentialValidationStatus.NetworkError),
        };

    private static IReadOnlyList<ModelDescriptor> BuildDefaultModels(ClaudeRuntimeSeed seed)
    {
        // The seed file lists ids only; model catalogs (Anthropic, etc.)
        // do not publish a context-window number that is stable across
        // model snapshots, so we leave ContextWindow null. Display name
        // is derived from the id since the wizard renders the id today
        // and changing that is a Phase-3 concern.
        var list = new List<ModelDescriptor>(seed.Models.Count);
        foreach (var id in seed.Models)
        {
            list.Add(new ModelDescriptor(id, id, ContextWindow: null));
        }
        return list;
    }
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