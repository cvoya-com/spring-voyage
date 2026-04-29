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
    /// <c>agents/dapr-agent/Dockerfile</c>. BYOI conformance path 3.
    /// </summary>
    /// <remarks>
    /// Issue #1106 verified (2026-04): the upstream <c>dapr-agents 1.0.1</c>
    /// PyPI package does NOT publish a runnable A2A entrypoint module —
    /// <c>dapr_agents/__init__.py</c> exports <c>DurableAgent</c>,
    /// <c>AgentRunner</c>, chat clients, and helpers, but no
    /// <c>dapr_agents.a2a</c> module. The A2A surface is provided by
    /// <c>a2a-sdk[http-server]</c>; agents wire their own ASGI app and
    /// expose it via uvicorn (see <c>agents/dapr-agent/agent.py</c> +
    /// <c>agents/dapr-agent/a2a_server.py</c>). If upstream ever adds a
    /// runnable A2A module, this argv can be swapped for
    /// <c>python -m dapr_agents.&lt;module&gt;</c> without changing the
    /// launcher contract.
    /// </remarks>
    internal static readonly string[] DefaultDaprAgentArgv = ["python", "agent.py"];

    private readonly ILogger _logger = loggerFactory.CreateLogger<DaprAgentLauncher>();

    /// <summary>YAML / definition <c>execution.tool</c> value for this launcher.</summary>
    public const string ToolId = "dapr-agent";

    /// <inheritdoc />
    public string Tool => ToolId;

    /// <inheritdoc />
    public Task<AgentLaunchSpec> PrepareAsync(
        AgentLaunchContext context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Prepared Dapr Agent launch request for agent {AgentId} thread {ThreadId}",
            context.AgentId, context.ThreadId);

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
            ["SPRING_THREAD_ID"] = context.ThreadId,
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
            // The Python `dapr` SDK defaults its gRPC client deadline to
            // 60 s (`DAPR_API_TIMEOUT_SECONDS`); the Conversation Alpha2
            // unary call inherits that deadline. With ~58 MCP tool
            // schemas in the prompt, llama3.2:3b on CPU takes far longer
            // than 60 s to produce its first response, so the call hits
            // the deadline and returns `DEADLINE_EXCEEDED` /
            // `Received RST_STREAM with error code 8` before the agent's
            // loop can make progress. 600 s gives Ollama on a slow CPU
            // enough headroom for a single LLM turn while still bounding
            // a hung sidecar.
            ["DAPR_API_TIMEOUT_SECONDS"] = "600",
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