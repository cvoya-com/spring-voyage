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
    /// <see cref="AgentLaunchSpec"/> describes the argv to run inside the
    /// container, the workspace the dispatcher must materialise (file contents
    /// keyed by relative path), the mount path inside the container, any extra
    /// env vars or volume mounts, and how the dispatcher should capture the
    /// agent's response. Launchers MUST NOT write to the local filesystem.
    /// </summary>
    Task<AgentLaunchSpec> PrepareAsync(AgentLaunchContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Inputs the dispatcher hands to a launcher for a single invocation.
/// </summary>
/// <param name="AgentId">The agent id (for logging and prompt materialisation).</param>
/// <param name="ThreadId">The thread id being served.</param>
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
    string ThreadId,
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
/// <remarks>
/// <para>
/// Replaces the legacy <c>AgentLaunchPrep</c> record. The new fields
/// (<see cref="Argv"/>, <see cref="User"/>, <see cref="StdinPayload"/>,
/// <see cref="A2APort"/>, <see cref="ResponseCapture"/>) all default to
/// behaviour-preserving values so the rename is wire- and code-equivalent
/// for callers that only set the original four fields. Subsequent PRs in
/// the #1087 series flow these new fields end-to-end through the dispatcher
/// and the A2A bridge so ephemeral agent dispatch can stop
/// <c>sleep infinity</c>-ing and actually invoke the agent tool.
/// </para>
/// </remarks>
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
/// <param name="Argv">
/// Optional argv vector the dispatcher should set as the container's
/// command. Each element becomes one argv entry — the dispatcher does not
/// shell-split the string. An empty list means "use the image's default
/// ENTRYPOINT/CMD" — the launch contract for images that already speak A2A
/// (e.g. <c>dapr-agents</c>) or images whose ENTRYPOINT is the Spring
/// agent-base bridge.
/// </param>
/// <param name="User">
/// Optional in-container user (uid[:gid] or username). When <c>null</c>,
/// the runtime uses the image's configured user.
/// </param>
/// <param name="StdinPayload">
/// Optional UTF-8 payload the dispatcher's bridge feeds on the agent
/// process's stdin. Used by launchers (e.g. <c>claude-code</c>) that pass
/// the prompt body via stdin rather than as a file or env var.
/// </param>
/// <param name="A2APort">
/// TCP port the in-container A2A endpoint listens on. The dispatcher uses
/// this for the readiness probe (<c>GET /.well-known/agent.json</c>) and
/// for the A2A <c>message/send</c> call. Defaults to 8999, which is what
/// the Spring agent-base bridge and <c>dapr-agents</c> both listen on.
/// </param>
/// <param name="ResponseCapture">
/// How the dispatcher should capture the agent's response. Defaults to
/// <see cref="AgentResponseCapture.A2A"/>; only A2A is wired today, the
/// other values exist so a future launcher can opt into a different
/// mechanism without bumping the launcher contract again.
/// </param>
public record AgentLaunchSpec(
    IReadOnlyDictionary<string, string> WorkspaceFiles,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    string WorkspaceMountPath,
    IReadOnlyList<string>? ExtraVolumeMounts = null,
    string? WorkingDirectory = null,
    IReadOnlyList<string>? Argv = null,
    string? User = null,
    string? StdinPayload = null,
    int A2APort = 8999,
    AgentResponseCapture ResponseCapture = AgentResponseCapture.A2A);

/// <summary>
/// How the dispatcher captures the agent's response from the container.
/// </summary>
/// <remarks>
/// Only <see cref="A2A"/> is wired today (and is the default). The other
/// values are reserved so a future launcher can opt into a different
/// capture mechanism without forcing a contract change. See ADR 0025
/// (introduced in PR 6 of the #1087 series).
/// </remarks>
public enum AgentResponseCapture
{
    /// <summary>
    /// Capture the response via an A2A <c>message/send</c> roundtrip on the
    /// in-container A2A endpoint (port <see cref="AgentLaunchSpec.A2APort"/>).
    /// This is the default and the only path implemented today.
    /// </summary>
    A2A,

    /// <summary>
    /// Capture the response by harvesting the container process's stdout
    /// after it exits. Reserved; not implemented.
    /// </summary>
    Stdout,

    /// <summary>
    /// Capture the response by reading a well-known file from the bind-mounted
    /// workspace after the container exits. Reserved; not implemented.
    /// </summary>
    VolumeDrop
}