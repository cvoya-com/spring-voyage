// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Prepares the container-launch contract (working directory, env vars, volume
/// mounts) for one specific external agent tool. Different tools (Claude Code,
/// Codex, Gemini CLI, …) materialise their configuration in different ways, so
/// each gets its own launcher. The dispatcher selects the launcher matching the
/// <see cref="AgentExecutionConfig.Tool"/> of the resolved agent definition.
/// </summary>
public interface IAgentToolLauncher
{
    /// <summary>
    /// The tool identifier this launcher handles (matches
    /// <see cref="AgentExecutionConfig.Tool"/>).
    /// </summary>
    string Tool { get; }

    /// <summary>
    /// Materialises a new per-invocation working directory on disk and returns
    /// the container-launch pieces that the dispatcher must splice into
    /// <see cref="ContainerConfig"/>. The returned <see cref="AgentLaunchPrep.WorkingDirectory"/>
    /// must be passed to <see cref="CleanupAsync"/> when the container exits.
    /// </summary>
    Task<AgentLaunchPrep> PrepareAsync(AgentLaunchContext context, CancellationToken cancellationToken = default);

    /// <summary>Deletes the working directory created by a prior <see cref="PrepareAsync"/> call.</summary>
    Task CleanupAsync(string workingDirectory, CancellationToken cancellationToken = default);
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
/// Output of <see cref="IAgentToolLauncher.PrepareAsync"/>.
/// </summary>
/// <param name="WorkingDirectory">Absolute path to the on-disk working directory the container mounts in.</param>
/// <param name="EnvironmentVariables">Env vars the dispatcher must add to the container (on top of its own baseline).</param>
/// <param name="VolumeMounts">Additional volume-mount specs (beyond the working-directory mount).</param>
public record AgentLaunchPrep(
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    IReadOnlyList<string> VolumeMounts);