// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// <see cref="IAgentToolLauncher"/> for the Dapr Agent container.  Materialises
/// a per-invocation working directory and sets the environment variables the
/// Python Dapr Agent expects: MCP endpoint/token, LLM provider/model, and the
/// assembled system prompt.
///
/// Unlike <see cref="ClaudeCodeLauncher"/> the Dapr Agent is an A2A-native
/// service and does not need a sidecar adapter — it exposes the A2A endpoint
/// directly.  The dispatcher reaches the agent on the container's
/// <c>AGENT_PORT</c> (default 8999).
/// </summary>
public class DaprAgentLauncher(
    IOptions<OllamaOptions> ollamaOptions,
    ILoggerFactory loggerFactory) : IAgentToolLauncher
{
    internal const string ContainerWorkspace = "/workspace";

    /// <summary>Default A2A port the Dapr Agent listens on.</summary>
    internal const int DefaultAgentPort = 8999;

    private readonly ILogger _logger = loggerFactory.CreateLogger<DaprAgentLauncher>();

    /// <inheritdoc />
    public string Tool => "dapr-agent";

    /// <inheritdoc />
    public Task<AgentLaunchPrep> PrepareAsync(
        AgentLaunchContext context,
        CancellationToken cancellationToken = default)
    {
        var workdir = Path.Combine(
            Path.GetTempPath(),
            "spring-dapr-agent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workdir);

        _logger.LogInformation(
            "Prepared Dapr Agent working directory {Workdir} for agent {AgentId} conversation {ConversationId}",
            workdir, context.AgentId, context.ConversationId);

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
            ["AGENT_PORT"] = DefaultAgentPort.ToString(),
        };

        // Pass the Ollama base URL so the Dapr Conversation component inside
        // the agent container can reach the Ollama instance.  The agent's Dapr
        // sidecar resolves this via the conversation-ollama.yaml component.
        if (!string.IsNullOrEmpty(opts.BaseUrl))
        {
            envVars["OLLAMA_ENDPOINT"] = opts.BaseUrl;
        }

        var mounts = new List<string>
        {
            $"{workdir}:{ContainerWorkspace}"
        };

        var prep = new AgentLaunchPrep(workdir, envVars, mounts);
        return Task.FromResult(prep);
    }

    /// <inheritdoc />
    public Task CleanupAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        try
        {
            if (Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, recursive: true);
                _logger.LogDebug("Deleted Dapr Agent working directory {Workdir}", workingDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to delete Dapr Agent working directory {Workdir}; leaving in place for operator inspection.",
                workingDirectory);
        }

        return Task.CompletedTask;
    }
}