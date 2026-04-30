// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Security.Cryptography;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Default platform-side builder for the <c>IAgentContext</c> bootstrap bundle
/// (D3a — Stage 3 of ADR-0029). Implements D1 spec § 2 for the OSS deployment.
/// </summary>
/// <remarks>
/// <para>
/// <b>Credential strategy:</b> every scoped token (Bucket-2, LLM provider, MCP)
/// is minted fresh per <see cref="BuildAsync"/> call using a cryptographically
/// random 32-byte value encoded as a URL-safe base-64 string. Tokens are
/// agent-scoped and per-launch: they are never reused across agent identities,
/// and successive launches of the same agent receive distinct tokens. The MCP
/// token is not generated here — it is minted by <see cref="IMcpServer"/> and
/// passed in via <see cref="AgentLaunchContext.McpToken"/> because the MCP
/// server owns the session registry.
/// </para>
/// <para>
/// <b>LLM provider URL resolution:</b> the builder first checks
/// <see cref="AgentContextOptions.LlmProviderUrl"/> (operator override). If
/// unset it falls back to <see cref="OllamaOptions.BaseUrl"/> (OSS default —
/// the platform-hosted Ollama instance). The resolved URL becomes
/// <c>SPRING_LLM_PROVIDER_URL</c>; the per-launch credential is a freshly
/// minted scoped token (the OSS Ollama deployment accepts any bearer token;
/// the cloud overlay replaces this builder with a tenant-KMS-backed variant
/// that issues signed tokens).
/// </para>
/// <para>
/// <b>Bucket-2 URL:</b> sourced from <see cref="AgentContextOptions.Bucket2Url"/>.
/// When unset the env var is omitted — the container will fail at
/// <c>initialize()</c> because <c>SPRING_BUCKET2_URL</c> is required per the
/// D1 spec. Operators must set <c>AgentContext:Bucket2Url</c> in production.
/// </para>
/// <para>
/// <b>Mounted files:</b> the agent definition YAML and tenant-config JSON are
/// delivered as workspace files under the canonical mount path
/// <c>/spring/context/</c> per D1 spec § 2.2.2. The launcher merges these
/// into its <see cref="AgentLaunchSpec.WorkspaceFiles"/> with the appropriate
/// sub-path prefix.
/// </para>
/// </remarks>
public class AgentContextBuilder(
    IMcpServer mcpServer,
    IOptions<AgentContextOptions> agentContextOptions,
    IOptions<OllamaOptions> ollamaOptions,
    ILoggerFactory loggerFactory) : IAgentContextBuilder
{
    /// <summary>
    /// Canonical mount directory for structured context files inside the
    /// container (D1 spec § 2.2.2). Relative path prefix used when building
    /// the <see cref="AgentBootstrapContext.ContextFiles"/> dictionary.
    /// </summary>
    public const string ContextMountPath = "/spring/context/";

    /// <summary>Filename for the agent-definition file (YAML).</summary>
    public const string AgentDefinitionFileName = "agent-definition.yaml";

    /// <summary>Filename for the tenant-config file (JSON).</summary>
    public const string TenantConfigFileName = "tenant-config.json";

    // Canonical env var names per D1 spec § 2.2.1.
    internal const string EnvTenantId = "SPRING_TENANT_ID";
    internal const string EnvUnitId = "SPRING_UNIT_ID";
    internal const string EnvAgentId = "SPRING_AGENT_ID";
    internal const string EnvThreadId = "SPRING_THREAD_ID";
    internal const string EnvBucket2Url = "SPRING_BUCKET2_URL";
    internal const string EnvBucket2Token = "SPRING_BUCKET2_TOKEN";
    internal const string EnvLlmProviderUrl = "SPRING_LLM_PROVIDER_URL";
    internal const string EnvLlmProviderToken = "SPRING_LLM_PROVIDER_TOKEN";
    internal const string EnvMcpUrl = "SPRING_MCP_URL";
    internal const string EnvMcpToken = "SPRING_MCP_TOKEN";
    internal const string EnvTelemetryUrl = "SPRING_TELEMETRY_URL";
    internal const string EnvTelemetryToken = "SPRING_TELEMETRY_TOKEN";
    internal const string EnvWorkspacePath = AgentVolumeManager.WorkspacePathEnvVar;
    internal const string EnvConcurrentThreads = "SPRING_CONCURRENT_THREADS";

    private readonly AgentContextOptions _agentContextOptions = agentContextOptions.Value;
    private readonly OllamaOptions _ollamaOptions = ollamaOptions.Value;
    private readonly ILogger _logger = loggerFactory.CreateLogger<AgentContextBuilder>();

    /// <inheritdoc />
    public Task<AgentBootstrapContext> BuildAsync(
        AgentLaunchContext launchContext,
        CancellationToken cancellationToken = default)
    {
        // Resolve platform-side endpoint URLs.
        var llmProviderUrl = ResolveLlmProviderUrl();
        var mcpUrl = mcpServer.Endpoint ?? string.Empty;

        // Mint per-launch, agent-scoped credentials.
        // The MCP token is pre-minted by the MCP server (passed in via launchContext).
        var bucket2Token = MintScopedToken();
        var llmProviderToken = MintScopedToken();

        var envVars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Static metadata.
            [EnvTenantId] = launchContext.TenantId,
            [EnvAgentId] = launchContext.AgentId,

            // Bucket-2 endpoint.
            [EnvBucket2Token] = bucket2Token,

            // Platform-provided service endpoints.
            [EnvLlmProviderUrl] = llmProviderUrl,
            [EnvLlmProviderToken] = llmProviderToken,
            [EnvMcpUrl] = mcpUrl,
            [EnvMcpToken] = launchContext.McpToken,

            // Workspace mount path (from AgentVolumeManager — D3c).
            [EnvWorkspacePath] = AgentVolumeManager.WorkspaceMountPath,

            // Concurrent-threads policy.
            [EnvConcurrentThreads] = launchContext.ConcurrentThreads ? "true" : "false",
        };

        // Optional fields: unit id, thread id, Bucket-2 URL, telemetry.
        if (!string.IsNullOrEmpty(launchContext.UnitId))
        {
            envVars[EnvUnitId] = launchContext.UnitId;
        }

        // Thread id — emitted when the launch is for a specific dispatch context
        // (e.g. first launch from the dispatcher). Absent on supervisor-driven
        // restarts, which are agent-level and not tied to a single thread.
        // D1 spec: SPRING_THREAD_ID (#1300).
        if (!string.IsNullOrEmpty(launchContext.ThreadId))
        {
            envVars[EnvThreadId] = launchContext.ThreadId;
        }

        if (!string.IsNullOrEmpty(_agentContextOptions.Bucket2Url))
        {
            envVars[EnvBucket2Url] = _agentContextOptions.Bucket2Url;
        }
        else
        {
            _logger.LogWarning(
                "AgentContext:Bucket2Url is not configured; SPRING_BUCKET2_URL will be absent from the " +
                "container bootstrap for agent {AgentId}. The container's initialize() will fail because " +
                "SPRING_BUCKET2_URL is required per the D1 spec § 2.2.1. Set AgentContext:Bucket2Url in " +
                "your deployment configuration.",
                launchContext.AgentId);
        }

        if (!string.IsNullOrEmpty(_agentContextOptions.TelemetryUrl))
        {
            envVars[EnvTelemetryUrl] = _agentContextOptions.TelemetryUrl;
        }

        if (!string.IsNullOrEmpty(_agentContextOptions.TelemetryToken))
        {
            envVars[EnvTelemetryToken] = _agentContextOptions.TelemetryToken;
        }

        // Build the mounted-file set for /spring/context/.
        var contextFiles = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!string.IsNullOrEmpty(launchContext.AgentDefinitionYaml))
        {
            contextFiles[AgentDefinitionFileName] = launchContext.AgentDefinitionYaml;
        }

        if (!string.IsNullOrEmpty(launchContext.TenantConfigJson))
        {
            contextFiles[TenantConfigFileName] = launchContext.TenantConfigJson;
        }

        _logger.LogInformation(
            "Built IAgentContext bootstrap for agent {AgentId} (tenant={TenantId} unit={UnitId} " +
            "thread={ThreadId} concurrent_threads={ConcurrentThreads} context_files={ContextFileCount})",
            launchContext.AgentId,
            launchContext.TenantId,
            launchContext.UnitId ?? "(none)",
            launchContext.ThreadId ?? "(none)",
            launchContext.ConcurrentThreads,
            contextFiles.Count);

        return Task.FromResult(new AgentBootstrapContext(envVars, contextFiles));
    }

    /// <inheritdoc />
    public Task<AgentBootstrapContext> RefreshForRestartAsync(
        SupervisorRestartContext restartContext,
        CancellationToken cancellationToken = default)
    {
        // Mint a fresh bootstrap bundle using the supervisor's persisted identity.
        // A supervisor-driven restart is agent-level, not thread-level, so we
        // supply a minimal synthetic AgentLaunchContext — no prompt, no thread id,
        // no agent-definition YAML, no tenant-config JSON.
        //
        // The MCP server requires a session bound to an agent + thread; we use a
        // synthetic restart-sentinel thread id so the MCP server can attribute tool
        // calls from the restarted container to a known session rather than to an
        // unknown caller. The supervisor does NOT cache the resulting tokens — they
        // are consumed immediately by RestartAsync per D1 spec § 2.2.3.

        _logger.LogInformation(
            "Refreshing IAgentContext credentials for supervisor restart of agent {AgentId} " +
            "(tenant={TenantId} unit={UnitId})",
            restartContext.AgentId,
            restartContext.TenantId,
            restartContext.UnitId ?? "(none)");

        // Issue a fresh MCP session for this restart. Use a synthetic thread id
        // that scopes the session to this restart event (not to any specific user
        // thread — the restarted agent will serve whichever thread dispatches next).
        var restartThreadId = $"restart:{restartContext.AgentId}:{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
        var mcpSession = mcpServer.IssueSession(restartContext.AgentId, restartThreadId);

        var syntheticLaunchContext = new AgentLaunchContext(
            AgentId: restartContext.AgentId,
            ThreadId: string.Empty,  // restarts are agent-level; SPRING_THREAD_ID omitted
            Prompt: string.Empty,
            McpEndpoint: mcpServer.Endpoint ?? string.Empty,
            McpToken: mcpSession.Token,
            TenantId: restartContext.TenantId,
            UnitId: restartContext.UnitId,
            ConcurrentThreads: restartContext.ConcurrentThreads);

        return BuildAsync(syntheticLaunchContext, cancellationToken);
    }

    /// <summary>
    /// Resolves the LLM provider endpoint URL from operator configuration.
    /// Falls back to the Ollama base URL (OSS default) when no override is set.
    /// </summary>
    private string ResolveLlmProviderUrl()
    {
        if (!string.IsNullOrEmpty(_agentContextOptions.LlmProviderUrl))
        {
            return _agentContextOptions.LlmProviderUrl;
        }

        // OSS default: deliver the platform-hosted Ollama base URL.
        return _ollamaOptions.BaseUrl;
    }

    /// <summary>
    /// Mints a fresh, cryptographically random, agent-scoped bearer token.
    /// 32 bytes → 43-character URL-safe base-64 string (no padding).
    /// </summary>
    private static string MintScopedToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}