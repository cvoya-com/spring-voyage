// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="IAgentToolLauncher"/> for OpenAI Codex containers. Describes a
/// per-invocation workspace containing:
/// <list type="bullet">
///   <item><c>AGENTS.md</c> — the assembled system prompt (all four layers).
///         Codex reads this file as its instructions equivalent of Claude Code's <c>CLAUDE.md</c>.</item>
///   <item><c>.mcp.json</c> — MCP server endpoint + bearer token the Codex agent will dial.</item>
/// </list>
/// The dispatcher materialises this workspace on its own host filesystem and
/// bind-mounts it at <c>/workspace</c> inside the container — see issue #1042.
/// <para>
/// <b>Expected container image shape:</b> The image must bundle the Codex CLI
/// and the A2A sidecar from <c>agents/a2a-sidecar/</c>. The sidecar wraps the
/// <c>codex</c> CLI binary, exposing it behind an A2A endpoint. The container
/// must read <c>AGENTS.md</c> and <c>.mcp.json</c> from the <c>/workspace</c>
/// mount and honour the <c>OPENAI_API_KEY</c> environment variable for
/// authentication with the OpenAI API.
/// </para>
/// </summary>
public class CodexLauncher(ILoggerFactory loggerFactory) : IAgentToolLauncher
{
    internal const string WorkspaceMountPath = "/workspace";
    private readonly ILogger _logger = loggerFactory.CreateLogger<CodexLauncher>();

    /// <inheritdoc />
    public string Tool => "codex";

    /// <inheritdoc />
    public Task<AgentLaunchPrep> PrepareAsync(
        AgentLaunchContext context,
        CancellationToken cancellationToken = default)
    {
        var mcpConfig = new
        {
            mcpServers = new Dictionary<string, object>
            {
                ["spring-voyage"] = new
                {
                    type = "http",
                    url = context.McpEndpoint,
                    headers = new Dictionary<string, string>
                    {
                        ["Authorization"] = $"Bearer {context.McpToken}"
                    }
                }
            }
        };

        var workspaceFiles = new Dictionary<string, string>
        {
            ["AGENTS.md"] = context.Prompt,
            [".mcp.json"] = JsonSerializer.Serialize(mcpConfig, new JsonSerializerOptions { WriteIndented = true })
        };

        _logger.LogInformation(
            "Prepared Codex workspace request ({FileCount} files) for agent {AgentId} conversation {ConversationId}",
            workspaceFiles.Count, context.AgentId, context.ConversationId);

        var envVars = new Dictionary<string, string>
        {
            ["SPRING_AGENT_ID"] = context.AgentId,
            ["SPRING_CONVERSATION_ID"] = context.ConversationId,
            ["SPRING_MCP_ENDPOINT"] = context.McpEndpoint,
            ["SPRING_AGENT_TOKEN"] = context.McpToken,
            ["SPRING_SYSTEM_PROMPT"] = context.Prompt
        };

        return Task.FromResult(new AgentLaunchPrep(
            WorkspaceFiles: workspaceFiles,
            EnvironmentVariables: envVars,
            WorkspaceMountPath: WorkspaceMountPath));
    }
}