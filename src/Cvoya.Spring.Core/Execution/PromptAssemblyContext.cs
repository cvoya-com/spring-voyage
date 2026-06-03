// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;

/// <summary>
/// Holds the per-agent input data needed for prompt assembly. The
/// assembler renders three sections — platform instructions, unit
/// context, and role-specific instructions — from this context.
/// </summary>
/// <param name="Policies">Optional unit policies as a JSON element.</param>
/// <param name="AgentInstructions">Optional agent-specific instructions for the role-specific instructions section.</param>
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
/// rendered as a sub-section of the unit-context section so the ordering
/// is: platform instructions → unit context (including unit bundle
/// prompts) → role-specific instructions. Bundle prompts are additive
/// and never interleave with agent-specific instructions.
/// </param>
/// <param name="AgentSkillBundles">
/// Optional ordered list of package-level skill bundles equipped directly
/// on the **agent** subject. Rendered as a sub-section of the
/// role-specific instructions section so the agent's own equipped skills
/// extend its role-specific guidance without conflating with the
/// unit-scoped <see cref="SkillBundles"/> rendered in the unit-context
/// section.
/// </param>
/// <param name="PendingAmendments">
/// Optional list of pending amendments queued for this agent.
/// </param>
/// <param name="ConnectorPromptFragments">
/// Optional ordered list of markdown fragments contributed by each
/// connector binding applicable to the launch subject. The assembler
/// concatenates these into a platform-instructions subsection telling
/// the agent what env vars its container has, what bound resource
/// identity each binding represents, and how to use the connector's CLI
/// tools.
/// </param>
/// <param name="IdentityPromptFragment">
/// Optional pre-rendered markdown fragment naming the launch subject's
/// identity (kind, address, display name, declared role / expertise /
/// parent units). Produced by <see cref="IIdentityPromptContextResolver"/>
/// at bundle build time so the assembled prompt every runtime sees names
/// the agent before the platform contract refers to "your assigned
/// role". The assembler renders the fragment after the platform contract
/// and before the connector-context subsection. <c>null</c> omits the
/// section entirely.
/// </param>
/// <param name="WorkspacePromptFragment">
/// Optional pre-rendered markdown fragment describing the per-runtime
/// container surface (workspace path, CLI tool baseline, session-storage
/// env vars, MCP discovery). Contributed by each
/// <see cref="IAgentRuntimeLauncher"/> via
/// <see cref="IAgentRuntimeLauncher.GetWorkspacePromptFragment"/>; the
/// assembler renders it under a fixed <c>### Container and workspace</c>
/// heading. <c>null</c> omits the section (the case for A2A-native
/// runtimes that have no container/workspace concept).
/// </param>
/// <param name="ConcurrentConversationsGuard">
/// When <c>true</c>, the assembler renders the platform-emitted
/// <c>### Concurrent conversations — per-conversation isolation</c>
/// sub-section inside the <c>## Platform Instructions</c> section
/// (ADR-0041 / #2096 / #2738 / #2745 / #3041). The guard names the two
/// things the platform isolates per conversation (private work
/// subdirectory + session continuity) and the constraints that follow
/// from what is shared (ephemeral ports, no process-global mutation).
/// Engineer-specific guidance (watcher commands, broad process kills)
/// lives in the <c>sv.engineer.defaults</c> bundle, not here. Defaults
/// to <c>false</c> so synthetic launch paths and tests that build a
/// sparse context do not accidentally surface the guard; the two
/// production callers
/// (<see cref="IAgentBootstrapBundleProvider"/> for the bundle path
/// and the per-actor dispatch context for the ephemeral path) set the
/// flag from <see cref="AgentExecutionConfig.ConcurrentThreads"/>.
/// </param>
/// <remarks>
/// <para>
/// Thread context (prior messages, sender display names, last
/// checkpoint) is not part of the assembled prompt: in every runtime we
/// ship, thread history lives in a runtime-native session-resume
/// mechanism (Claude Code's <c>--resume</c>, the Python SDK's runtime
/// API, equivalents in Codex and Gemini). Replicating it in the
/// assembled prompt was both duplicate with that native state and
/// incompatible with the per-agent content-addressable bootstrap bundle
/// — a per-turn-changing prompt would churn the bundle hash every turn
/// and defeat the 304 fast path the bridge relies on. The peer-directory
/// member list that used to live on this record was also removed once
/// the runtime gained the <c>sv.directory.*</c> tools.
/// </para>
/// <para>
/// The <c>Skills</c> projection of <see cref="ISkillRegistry"/> was
/// removed in #2670: the assembler no longer renders a per-registry
/// catalog (the duplicate <c>**sv**:</c> headers and the auto-generated
/// "Tools exposed by the X connector." strings). The always-available
/// platform-tool catalog now lives in the platform-instructions section
/// (<see cref="IPlatformPromptProvider"/>); category-aware discovery for
/// everything else happens at runtime via
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
    IReadOnlyList<string>? ConnectorPromptFragments = null,
    string? IdentityPromptFragment = null,
    string? WorkspacePromptFragment = null,
    bool ConcurrentConversationsGuard = false);
