// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// <see cref="IAgentToolLauncher"/> for the Dapr Agent container. Sets the
/// environment variables the Python Dapr Agent expects: MCP endpoint/token,
/// LLM provider/model, and the assembled system prompt. The dispatcher
/// materialises an empty per-invocation workspace and bind-mounts it at
/// <c>/workspace</c> — the Dapr Agent currently consumes its prompt via
/// <c>SPRING_SYSTEM_PROMPT</c>, but the workspace mount keeps the launch
/// shape uniform across tool launchers.
///
/// Unlike <see cref="ClaudeCodeLauncher"/> the Dapr Agent is an A2A-native
/// service and does not need a sidecar adapter — it exposes the A2A endpoint
/// directly. The dispatcher reaches the agent on the container's
/// <c>AGENT_PORT</c> (default 8999).
///
/// PR 4 of the #1087 series wires the launcher to BYOI conformance path 3:
/// the spec sets a non-empty <see cref="AgentLaunchSpec.Argv"/> that bypasses
/// the agent-base bridge entirely and hands control directly to the Python
/// process that already speaks A2A natively.
/// </summary>
public class DaprAgentLauncher(
    IOptions<OllamaOptions> ollamaOptions,
    ILoggerFactory loggerFactory) : IAgentToolLauncher
{
    internal const string WorkspaceMountPath = "/workspace";

    /// <summary>Default A2A port the Dapr Agent listens on.</summary>
    internal const int DefaultAgentPort = 8999;

    /// <summary>
    /// Argv vector that bypasses the agent-base bridge and starts the Dapr
    /// Agent process directly. Matches the CMD declared by
    /// <c>agents/dapr-agent/Dockerfile</c> (the actual production entrypoint
    /// for this image — the upstream <c>dapr-agents</c> PyPI package does
    /// not publish a runnable A2A module today, so we run our own
    /// <c>agent.py</c> shim instead). BYOI conformance path 3.
    /// </summary>
    /// <remarks>
    /// See follow-up issue tracking confirmation of the upstream
    /// <c>dapr-agents</c> A2A entrypoint module path; if/when that lands,
    /// this argv can be swapped for <c>python -m dapr_agents.&lt;module&gt;</c>
    /// without changing the launcher contract.
    /// </remarks>
    internal static readonly string[] DefaultDaprAgentArgv = ["python", "agent.py"];

    private readonly ILogger _logger = loggerFactory.CreateLogger<DaprAgentLauncher>();

    /// <inheritdoc />
    public string Tool => "dapr-agent";

    /// <inheritdoc />
    public Task<AgentLaunchSpec> PrepareAsync(
        AgentLaunchContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Prepared Dapr Agent launch request for agent {AgentId} conversation {ConversationId}",
            context.AgentId, context.ConversationId);

        var opts = ollamaOptions.Value;

        // Provider / model selection is YAML-driven via AgentDefinition.Execution:
        // when the definition specifies execution.provider / execution.model those win.
        // Otherwise the launcher falls back to Ollama defaults so existing definitions
        // without the fields continue to work. These env vars map to the Dapr
        // Conversation component name ("llm-provider") and model metadata consumed by
        // the Python agent; changing provider is a YAML-only change (#480 acceptance).
        var provider = !string.IsNullOrWhiteSpace(context.Provider) ? context.Provider! : "ollama";
        var model = !string.IsNullOrWhiteSpace(context.Model)
            ? context.Model!
            : opts.DefaultModel ?? "llama3.2:3b";

        var envVars = new Dictionary<string, string>
        {
            ["SPRING_AGENT_ID"] = context.AgentId,
            ["SPRING_CONVERSATION_ID"] = context.ConversationId,
            ["SPRING_MCP_ENDPOINT"] = context.McpEndpoint,
            ["SPRING_AGENT_TOKEN"] = context.McpToken,
            ["SPRING_SYSTEM_PROMPT"] = context.Prompt,
            ["SPRING_MODEL"] = model,
            ["SPRING_LLM_PROVIDER"] = provider,
            // AGENT_PORT is the env var the in-container agent.py binds to
            // (see agents/dapr-agent/Dockerfile). DAPR_AGENT_PORT is the
            // contract name introduced by issue #1097 — kept alongside
            // AGENT_PORT for back-compat with existing deployments while
            // PR 5 cuts the dispatcher over to the new field.
            ["AGENT_PORT"] = DefaultAgentPort.ToString(),
            ["DAPR_AGENT_PORT"] = DefaultAgentPort.ToString(),
        };

        // Pass the Ollama base URL so the Dapr Conversation component inside
        // the agent container can reach the Ollama instance.  The agent's Dapr
        // sidecar resolves this via the conversation-ollama.yaml component.
        if (!string.IsNullOrEmpty(opts.BaseUrl))
        {
            envVars["OLLAMA_ENDPOINT"] = opts.BaseUrl;
        }

        return Task.FromResult(new AgentLaunchSpec(
            WorkspaceFiles: new Dictionary<string, string>(),
            EnvironmentVariables: envVars,
            WorkspaceMountPath: WorkspaceMountPath,
            // Non-empty argv: skip the agent-base bridge ENTRYPOINT and
            // hand control directly to the Python process that already
            // speaks A2A on :8999. BYOI conformance path 3.
            Argv: DefaultDaprAgentArgv,
            // Dapr Agent receives messages via A2A, not stdin.
            StdinPayload: null));
    }
}