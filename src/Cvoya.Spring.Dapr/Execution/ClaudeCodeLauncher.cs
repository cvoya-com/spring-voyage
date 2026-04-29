// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging;

/// <summary>
/// <see cref="IAgentToolLauncher"/> for Claude Code containers. Describes a
/// per-invocation workspace containing:
/// <list type="bullet">
///   <item><c>CLAUDE.md</c> — the assembled system prompt (all four layers).</item>
///   <item><c>.mcp.json</c> — MCP server endpoint + bearer token Claude Code will dial.</item>
/// </list>
/// The dispatcher materialises this workspace on its own host filesystem and
/// bind-mounts it at <c>/workspace</c> inside the container — see issue #1042.
///
/// PR 4 of the #1087 series wires the launcher to the BYOI conformance
/// path 1: the spec leaves <see cref="AgentLaunchSpec.Argv"/> empty so the
/// agent-base image's ENTRYPOINT (the TypeScript A2A bridge) takes over and
/// re-execs the real CLI from <c>SPRING_AGENT_ARGV</c>. The launcher also
/// surfaces the assembled prompt as <see cref="AgentLaunchSpec.StdinPayload"/>
/// so PR 5 can flow it through the bridge to <c>claude</c>'s stdin.
/// </summary>
public class ClaudeCodeLauncher(ILoggerFactory loggerFactory) : IAgentToolLauncher
{
    internal const string WorkspaceMountPath = "/workspace";

    /// <summary>
    /// Argv vector the A2A bridge (agent-base ENTRYPOINT) spawns inside the
    /// container on every <c>message/send</c>. Encoded as a JSON array string
    /// in <c>SPRING_AGENT_ARGV</c> so the bridge can recover the exact
    /// quoting/whitespace without shell-splitting (see #1063 for why we
    /// avoid string-split argv).
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><c>--print</c> drives <c>claude</c> in non-interactive mode so
    ///   it consumes stdin and writes to stdout instead of opening a TUI.</item>
    ///   <item><c>--dangerously-skip-permissions</c> waives the per-tool
    ///   confirmation prompt — the container is the sandbox.</item>
    ///   <item><c>--output-format stream-json</c> emits structured JSON the
    ///   dispatcher can map to <see cref="Cvoya.Spring.Core.Messaging.StreamEvent"/>s.</item>
    /// </list>
    /// Source: matches the smoke argv used by the agent-sidecar config tests
    /// (<c>deployment/agent-sidecar/test/config.test.ts</c>) and is the
    /// BYOI path-1 baseline documented in #1097. Since PR 5 of #1087
    /// (#1098) the dispatcher no longer runs <c>sleep infinity</c>: the
    /// argv below is JSON-encoded into <c>SPRING_AGENT_ARGV</c> and
    /// exec'd by the agent-base bridge on every <c>message/send</c>,
    /// with the user's prompt fed via stdin.
    /// </remarks>
    internal static readonly string[] DefaultClaudeArgv =
    [
        "claude",
        "--print",
        "--dangerously-skip-permissions",
        "--output-format",
        "stream-json"
    ];

    private readonly ILogger _logger = loggerFactory.CreateLogger<ClaudeCodeLauncher>();

    /// <inheritdoc />
    public string Tool => "claude-code";

    /// <inheritdoc />
    public Task<AgentLaunchSpec> PrepareAsync(
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
            ["CLAUDE.md"] = context.Prompt,
            [".mcp.json"] = JsonSerializer.Serialize(mcpConfig, new JsonSerializerOptions { WriteIndented = true })
        };

        _logger.LogInformation(
            "Prepared Claude Code workspace request ({FileCount} files) for agent {AgentId} thread {ThreadId}",
            workspaceFiles.Count, context.AgentId, context.ThreadId);

        var envVars = new Dictionary<string, string>
        {
            ["SPRING_AGENT_ID"] = context.AgentId,
            ["SPRING_THREAD_ID"] = context.ThreadId,
            ["SPRING_MCP_ENDPOINT"] = context.McpEndpoint,
            ["SPRING_AGENT_TOKEN"] = context.McpToken,
            ["SPRING_SYSTEM_PROMPT"] = context.Prompt,
            // The bridge parses this back into argv via JSON.parse — see
            // deployment/agent-sidecar/src/config.ts. Hand-rolling the
            // encoding is forbidden (see issue text); JsonSerializer
            // gives us stable, double-quoted output.
            ["SPRING_AGENT_ARGV"] = JsonSerializer.Serialize(DefaultClaudeArgv)
        };

        return Task.FromResult(new AgentLaunchSpec(
            WorkspaceFiles: workspaceFiles,
            EnvironmentVariables: envVars,
            WorkspaceMountPath: WorkspaceMountPath,
            // Empty argv: defer to the agent-base image's ENTRYPOINT (the
            // TypeScript bridge), which reads SPRING_AGENT_ARGV and spawns
            // the real CLI per `message/send`. BYOI conformance path 1.
            Argv: Array.Empty<string>(),
            // Same content as CLAUDE.md / SPRING_SYSTEM_PROMPT — the bridge
            // (PR 5) will pipe this to `claude`'s stdin alongside the per-
            // message user text. Populated here so PR 5 can wire it up
            // without touching the launcher contract again.
            StdinPayload: context.Prompt));
    }
}