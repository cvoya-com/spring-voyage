// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="IAgentToolLauncher"/> for Claude Code containers. Materialises a
/// per-invocation working directory containing:
/// <list type="bullet">
///   <item><c>CLAUDE.md</c> — the assembled system prompt (all four layers).</item>
///   <item><c>.mcp.json</c> — MCP server endpoint + bearer token Claude Code will dial.</item>
/// </list>
/// The directory is bind-mounted at <c>/workspace</c> inside the container and
/// <see cref="CleanupAsync"/> removes it after the run completes.
/// </summary>
public class ClaudeCodeLauncher(ILoggerFactory loggerFactory) : IAgentToolLauncher
{
    internal const string WorkspaceMountPath = "/workspace";
    private readonly ILogger _logger = loggerFactory.CreateLogger<ClaudeCodeLauncher>();

    /// <inheritdoc />
    public string Tool => "claude-code";

    /// <inheritdoc />
    public async Task<AgentLaunchPrep> PrepareAsync(
        AgentLaunchContext context,
        CancellationToken cancellationToken = default)
    {
        var workdir = Path.Combine(
            Path.GetTempPath(),
            "spring-claude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workdir);

        await File.WriteAllTextAsync(
            Path.Combine(workdir, "CLAUDE.md"),
            context.Prompt,
            cancellationToken);

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

        await File.WriteAllTextAsync(
            Path.Combine(workdir, ".mcp.json"),
            JsonSerializer.Serialize(mcpConfig, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);

        _logger.LogInformation(
            "Prepared Claude Code working directory {Workdir} for agent {AgentId} conversation {ConversationId}",
            workdir, context.AgentId, context.ConversationId);

        var envVars = new Dictionary<string, string>
        {
            ["SPRING_AGENT_ID"] = context.AgentId,
            ["SPRING_CONVERSATION_ID"] = context.ConversationId,
            ["SPRING_MCP_ENDPOINT"] = context.McpEndpoint,
            ["SPRING_AGENT_TOKEN"] = context.McpToken,
            ["SPRING_SYSTEM_PROMPT"] = context.Prompt
        };

        var mounts = new List<string>
        {
            $"{workdir}:{WorkspaceMountPath}"
        };

        return new AgentLaunchPrep(workdir, envVars, mounts);
    }

    /// <inheritdoc />
    public Task CleanupAsync(string workingDirectory, CancellationToken cancellationToken = default)
    {
        try
        {
            if (Directory.Exists(workingDirectory))
            {
                Directory.Delete(workingDirectory, recursive: true);
                _logger.LogDebug("Deleted Claude Code working directory {Workdir}", workingDirectory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to delete Claude Code working directory {Workdir}; leaving in place for operator inspection.",
                workingDirectory);
        }

        return Task.CompletedTask;
    }
}