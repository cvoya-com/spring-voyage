// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// A launcher's contribution to an agent's bootstrap bundle (ADR-0055 §3).
/// The runtime-specific files an agent needs at workspace launch — the
/// system-prompt file (<c>CLAUDE.md</c> / <c>AGENTS.md</c> / <c>GEMINI.md</c>)
/// and the MCP config file (<c>.mcp.json</c>, <c>.gemini/settings.json</c>) —
/// live here rather than on <see cref="AgentLaunchSpec"/>. The bundle
/// provider composes contributions from every launcher (selected by the
/// agent's runtime) into the single content-addressable bundle the
/// sidecar pulls.
/// </summary>
/// <param name="Files">
/// File contents keyed by workspace-relative path (e.g. <c>"CLAUDE.md"</c>,
/// <c>".mcp.json"</c>). Empty when the launcher does not own any
/// in-workspace files — the A2A-native <c>spring-voyage-agent</c>
/// launcher returns an empty contribution.
/// </param>
/// <param name="PlatformFilePaths">
/// Subset of <see cref="Files"/> the sidecar pins per-turn via the
/// integrity check (ADR-0055 §6). The bundle provider hashes each named
/// file and emits the path → hash mapping in
/// <see cref="AgentBootstrapBundle.PlatformFileHashes"/>.
/// </param>
public record AgentBootstrapContribution(
    IReadOnlyDictionary<string, string> Files,
    IReadOnlyList<string> PlatformFilePaths)
{
    /// <summary>An empty contribution — no in-workspace files.</summary>
    public static AgentBootstrapContribution Empty { get; } =
        new(new Dictionary<string, string>(StringComparer.Ordinal), Array.Empty<string>());
}

/// <summary>
/// Inputs the bundle provider hands to a launcher when composing its
/// contribution. Per ADR-0055 the bundle is keyed on
/// <see cref="AgentId"/> only — there is no turn or message context;
/// per-turn data (MCP session token, user message text) rides the A2A
/// wire.
/// </summary>
/// <param name="AgentId">The agent identifier in canonical Guid wire form.</param>
/// <param name="Definition">The agent definition the bundle is being built for.</param>
/// <param name="McpEndpoint">
/// Worker-issued MCP endpoint URL the agent container will dial. Stamped
/// into the launcher's MCP config file with an empty
/// <c>Authorization</c> header — the per-turn MCP session token is
/// written by the sidecar from each turn's A2A
/// <c>message/send</c> metadata (ADR-0055 §4 / ADR-0052 §4).
/// </param>
/// <param name="AssembledSystemPrompt">
/// The per-agent system prompt produced by <see cref="IPromptAssembler"/>
/// — platform instructions + unit context + role-specific instructions
/// and equipped skill bundles. The bundle provider invokes the assembler
/// once per bundle build and hands the resulting string here; CLI
/// launchers write it to their runtime's auto-discovered system-prompt
/// file (<c>CLAUDE.md</c> for Claude Code, <c>AGENTS.md</c> for Codex,
/// <c>GEMINI.md</c> for Gemini). Thread history is NOT in this string —
/// each runtime's session-resume mechanism delivers it.
/// </param>
public record AgentBootstrapContributionContext(
    string AgentId,
    AgentDefinition Definition,
    string McpEndpoint,
    string AssembledSystemPrompt);
