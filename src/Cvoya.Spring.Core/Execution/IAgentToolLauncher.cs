// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Describes the container-launch contract for one specific external agent
/// tool. Different tools (Claude Code, Codex, Gemini CLI, …) materialise
/// their per-invocation configuration differently, so each gets its own
/// launcher. The dispatcher selects the launcher matching the
/// <see cref="AgentExecutionConfig.Tool"/> of the resolved agent definition.
/// </summary>
/// <remarks>
/// Launchers no longer touch the local filesystem: they describe the workspace
/// they need (file contents keyed by relative path, plus the desired in-container
/// mount path) and let the dispatcher service materialise that workspace on its
/// own host filesystem. This is what allows the agent container's bind mount to
/// resolve to a real path the container runtime can see — see issue #1042.
/// </remarks>
public interface IAgentToolLauncher
{
    /// <summary>
    /// The tool identifier this launcher handles (matches
    /// <see cref="AgentExecutionConfig.Tool"/>).
    /// </summary>
    string Tool { get; }

    /// <summary>
    /// Builds the container-launch contract for one invocation. The returned
    /// <see cref="AgentLaunchPrep"/> describes the workspace the dispatcher
    /// must materialise (file contents keyed by relative path), the mount
    /// path inside the container, and any extra env vars or volume mounts.
    /// Launchers MUST NOT write to the local filesystem.
    /// </summary>
    Task<AgentLaunchPrep> PrepareAsync(AgentLaunchContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Inputs the dispatcher hands to a launcher for a single invocation.
/// </summary>
/// <param name="AgentId">The agent id (for logging and prompt materialisation).</param>
/// <param name="ConversationId">The conversation id being served.</param>
/// <param name="Prompt">The assembled system prompt (Layer 1–4).</param>
/// <param name="McpEndpoint">The URL the container should use to reach the MCP server.</param>
/// <param name="McpToken">The bearer token the container must present on MCP calls.</param>
/// <param name="Provider">
/// Optional LLM provider selector from the agent's <see cref="AgentExecutionConfig.Provider"/>.
/// Launchers that front a Dapr Conversation runtime (e.g. the Dapr Agent) use
/// this to pin the component by name. <c>null</c> means "use launcher default".
/// Launchers that do not route through Dapr Conversation may ignore this field.
/// </param>
/// <param name="Model">
/// Optional model identifier from the agent's <see cref="AgentExecutionConfig.Model"/>.
/// <c>null</c> means "use launcher default".
/// </param>
public record AgentLaunchContext(
    string AgentId,
    string ConversationId,
    string Prompt,
    string McpEndpoint,
    string McpToken,
    string? Provider = null,
    string? Model = null);

/// <summary>
/// Output of <see cref="IAgentToolLauncher.PrepareAsync"/>. Pure data — no
/// on-disk state. The dispatcher materialises <see cref="WorkspaceFiles"/>
/// into a fresh per-invocation directory on its own filesystem and bind-mounts
/// it at <see cref="WorkspaceMountPath"/> inside the container.
/// </summary>
/// <param name="WorkspaceFiles">
/// File contents keyed by path relative to the workspace root
/// (e.g. <c>"CLAUDE.md"</c>, <c>".mcp.json"</c>). Empty when the agent does
/// not need a workspace materialised.
/// </param>
/// <param name="EnvironmentVariables">Env vars the dispatcher must add to the container (on top of its own baseline).</param>
/// <param name="WorkspaceMountPath">
/// Absolute path inside the container where the dispatcher must bind-mount
/// the materialised workspace (e.g. <c>"/workspace"</c>). Required whenever
/// <see cref="WorkspaceFiles"/> is non-empty.
/// </param>
/// <param name="ExtraVolumeMounts">Additional volume-mount specs (beyond the workspace mount).</param>
/// <param name="WorkingDirectory">
/// Optional working directory inside the container. When <c>null</c>, the
/// dispatcher uses <see cref="WorkspaceMountPath"/>.
/// </param>
public record AgentLaunchPrep(
    IReadOnlyDictionary<string, string> WorkspaceFiles,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    string WorkspaceMountPath,
    IReadOnlyList<string>? ExtraVolumeMounts = null,
    string? WorkingDirectory = null);