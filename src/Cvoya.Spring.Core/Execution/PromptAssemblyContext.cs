// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;

/// <summary>
/// Holds the per-agent input data needed for prompt assembly. The
/// assembler renders three layers — platform (Layer 1), unit context
/// (Layer 2), and agent instructions (Layer 4) — from this context.
/// </summary>
/// <param name="Policies">Optional unit policies as a JSON element.</param>
/// <param name="AgentInstructions">Optional agent-specific instructions (Layer 4).</param>
/// <param name="EffectiveMetadata">
/// The agent's effective configuration for this particular message turn,
/// i.e. the merge of the agent's global <see cref="AgentMetadata"/> with any
/// per-membership override recorded on the <c>(sender-unit, agent)</c> edge.
/// Downstream consumers that need to pick a model, a specialty, or an
/// execution mode for the turn should read from here rather than re-reading
/// the agent's global state.
/// </param>
/// <param name="SkillBundles">
/// Optional ordered list of package-level skill bundles equipped on the
/// **unit**. Each bundle contributes a prompt fragment and a list of
/// required tools. Prompts are concatenated in declaration order and
/// rendered as a sub-section of Layer 2 (unit context) so the ordering is:
/// platform → unit context (including unit bundle prompts) → agent
/// instructions. Bundle prompts are additive and never interleave with
/// agent-specific instructions.
/// </param>
/// <param name="AgentSkillBundles">
/// Optional ordered list of package-level skill bundles equipped directly
/// on the **agent** subject. Rendered as a sub-section of Layer 4 (agent
/// instructions) so the agent's own equipped skills extend its role-
/// specific guidance without conflating with the unit-scoped
/// <see cref="SkillBundles"/> rendered in Layer 2.
/// </param>
/// <param name="PendingAmendments">
/// Optional list of pending amendments queued for this agent.
/// </param>
/// <param name="ConnectorPromptFragments">
/// Optional ordered list of markdown fragments contributed by each
/// connector binding applicable to the launch subject. The assembler
/// concatenates these into a platform-layer subsection telling the agent
/// what env vars its container has, what bound resource identity each
/// binding represents, and how to use the connector's CLI tools.
/// </param>
/// <remarks>
/// <para>
/// Layer 3 (thread context — prior messages, sender display names,
/// last checkpoint) was removed: in every runtime we ship, thread
/// history lives in a runtime-native session-resume mechanism
/// (Claude Code's <c>--resume</c>, the Python SDK's runtime API,
/// equivalents in Codex and Gemini). Replicating it in the assembled
/// prompt was both duplicate with that native state and incompatible
/// with the per-agent content-addressable bootstrap bundle — a
/// per-turn-changing prompt would churn the bundle hash every turn
/// and defeat the 304 fast path the bridge relies on. The peer-
/// directory member list that used to live on this record was also
/// removed once the runtime gained the <c>sv.directory.*</c> tools.
/// </para>
/// <para>
/// The <c>Skills</c> projection of <see cref="ISkillRegistry"/> was
/// removed in #2670: the assembler no longer renders a per-registry
/// catalog (the duplicate <c>**sv**:</c> headers and the auto-generated
/// "Tools exposed by the X connector." strings). The always-available
/// platform-tool catalog now lives in Layer 1
/// (<see cref="IPlatformPromptProvider"/>); category-aware discovery
/// for everything else happens at runtime via
/// <c>sv.tools.list_categories</c> / <c>sv.tools.list(&lt;category&gt;)</c>.
/// </para>
/// </remarks>
public record PromptAssemblyContext(
    JsonElement? Policies,
    string? AgentInstructions,
    AgentMetadata? EffectiveMetadata = null,
    IReadOnlyList<SkillBundle>? SkillBundles = null,
    IReadOnlyList<SkillBundle>? AgentSkillBundles = null,
    IReadOnlyList<PendingAmendment>? PendingAmendments = null,
    IReadOnlyList<string>? ConnectorPromptFragments = null);
