// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Security.Cryptography;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Mcp;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Default platform-side builder for the <c>IAgentContext</c> bootstrap bundle
/// (D3a — Stage 3 of ADR-0029). Implements D1 spec § 2 for the OSS deployment.
/// </summary>
/// <remarks>
/// <para>
/// <b>Credential strategy:</b> the scoped tokens (Bucket-2, LLM provider) are
/// minted fresh per <see cref="BuildAsync"/> call using a cryptographically
/// random 32-byte value encoded as a URL-safe base-64 string. Tokens are
/// agent-scoped and per-launch: they are never reused across agent identities,
/// and successive launches of the same agent receive distinct tokens. The MCP
/// token is not generated here — it is passed in via
/// <see cref="AgentLaunchContext.McpToken"/>. ADR-0052 §3: the persistent-agent
/// deploy path supplies an empty MCP token (a freshly-deployed container has no
/// turn context to authorise); a real per-turn session token is delivered on
/// the first dispatched turn (PR 3 / #2615). The MCP endpoint URL is resolved
/// from <see cref="McpServerOptions"/> — this builder is an endpoint-only
/// consumer and does not co-reside with the started worker-side McpServer.
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
    IOptions<McpServerOptions> mcpServerOptions,
    IOptions<AgentContextOptions> agentContextOptions,
    IOptions<OllamaOptions> ollamaOptions,
    IAgentBootstrapAuthStore bootstrapAuthStore,
    ILoggerFactory loggerFactory) : IAgentContextBuilder
{
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
    internal const string EnvWorkspacePath = AgentWorkspaceContract.WorkspacePathEnvVar;
    internal const string EnvBootstrapUrl = AgentWorkspaceContract.BootstrapUrlEnvVar;
    internal const string EnvBootstrapToken = AgentWorkspaceContract.BootstrapTokenEnvVar;
    internal const string EnvConcurrentThreads = "SPRING_CONCURRENT_THREADS";

    private readonly McpServerOptions _mcpServerOptions = mcpServerOptions.Value;
    private readonly AgentContextOptions _agentContextOptions = agentContextOptions.Value;
    private readonly OllamaOptions _ollamaOptions = ollamaOptions.Value;
    private readonly IAgentBootstrapAuthStore _bootstrapAuthStore = bootstrapAuthStore
        ?? throw new ArgumentNullException(nameof(bootstrapAuthStore));
    private readonly ILogger _logger = loggerFactory.CreateLogger<AgentContextBuilder>();

    /// <inheritdoc />
    public Task<AgentBootstrapContext> BuildAsync(
        AgentLaunchContext launchContext,
        CancellationToken cancellationToken = default)
    {
        // Resolve platform-side endpoint URLs.
        var llmProviderUrl = ResolveLlmProviderUrl();
        // ADR-0052 §3: the container-facing MCP endpoint is derived from
        // configuration, not the live listener — the single McpServer hosted
        // service runs worker-only and this builder is an endpoint-only
        // consumer that may run without a started listener.
        var mcpUrl = _mcpServerOptions.ContainerEndpoint;

        // Mint per-launch, agent-scoped credentials.
        // The MCP token is supplied via launchContext (empty on the deploy
        // path; a per-turn session token on dispatch — ADR-0052 §3).
        var bucket2Token = MintScopedToken();
        var llmProviderToken = MintScopedToken();

        var envVars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Static metadata.
            [EnvTenantId] = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(launchContext.TenantId),
            [EnvAgentId] = launchContext.AgentId,

            // Bucket-2 endpoint.
            [EnvBucket2Token] = bucket2Token,

            // Platform-provided service endpoints.
            [EnvLlmProviderUrl] = llmProviderUrl,
            [EnvLlmProviderToken] = llmProviderToken,
            [EnvMcpUrl] = mcpUrl,
            [EnvMcpToken] = launchContext.McpToken,

            // ADR-0055 §5: per-member workspace mount path.
            [EnvWorkspacePath] = AgentWorkspaceContract.BuildMountPath(launchContext.AgentId),

            // ADR-0055 §8/§9: worker bootstrap endpoint URL + per-agent
            // bearer token. The sidecar's bootstrap client pulls the
            // bundle from this URL on container start.
            [EnvBootstrapUrl] = _mcpServerOptions.BuildBootstrapEndpoint(launchContext.AgentId),
            [EnvBootstrapToken] = _bootstrapAuthStore.Issue(launchContext.AgentId),

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

        // ADR-0055: the agent-definition YAML and tenant-config JSON used
        // to ride here under ContextFiles. They now live in the agent
        // bootstrap bundle the sidecar pulls, written under the per-member
        // workspace mount. This builder owns env vars only.
        _logger.LogInformation(
            "Built IAgentContext env-var set for agent {AgentId} (tenant={TenantId} unit={UnitId} " +
            "thread={ThreadId} concurrent_threads={ConcurrentThreads})",
            launchContext.AgentId,
            launchContext.TenantId,
            launchContext.UnitId ?? "(none)",
            launchContext.ThreadId ?? "(none)",
            launchContext.ConcurrentThreads);

        return Task.FromResult(new AgentBootstrapContext(envVars));
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
