// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;

/// <summary>
/// Holds all input data needed for prompt assembly across the four layers.
/// </summary>
/// <param name="Policies">Optional unit policies as a JSON element.</param>
/// <param name="Skills">Optional skills available to the agent.</param>
/// <param name="PriorMessages">Prior messages in the conversation.</param>
/// <param name="LastCheckpoint">Optional last checkpoint state.</param>
/// <param name="AgentInstructions">Optional agent-specific instructions (Layer 4).</param>
/// <param name="EffectiveMetadata">
/// The agent's effective configuration for this particular message turn,
/// i.e. the merge of the agent's global <see cref="AgentMetadata"/> with any
/// per-membership override recorded on the <c>(sender-unit, agent)</c> edge
/// (see #160 / #243). When the sender is not a unit, this falls back to the
/// agent's global metadata. Downstream consumers that need to pick a model,
/// a specialty, or an execution mode for the turn should read from here
/// rather than re-reading the agent's global state.
/// </param>
/// <param name="SkillBundles">
/// Optional ordered list of package-level skill bundles equipped on the
/// **unit** (see #167 / #2360). Each bundle contributes a prompt fragment
/// and a list of required tools. Prompts are concatenated in declaration
/// order and rendered as a sub-section of Layer 2 (unit context) so the
/// ordering is: platform → unit context (including unit bundle prompts)
/// → conversation → agent instructions. The surrounding layer order
/// matches the existing four-layer assembly; bundle prompts are additive
/// and never interleave with agent-specific instructions.
/// </param>
/// <param name="AgentSkillBundles">
/// Optional ordered list of package-level skill bundles equipped directly
/// on the **agent** subject (see #2360). Rendered as a sub-section of
/// Layer 4 (agent instructions) so the agent's own equipped skills
/// extend its role-specific guidance without conflating with the unit-
/// scoped <see cref="SkillBundles"/> rendered in Layer 2. Member agents
/// see both: the unit's bundles via Layer 2 and their own via Layer 4,
/// with no explicit inheritance table needed.
/// </param>
/// <param name="PendingAmendments">
/// Optional list of pending amendments queued for this agent.
/// </param>
/// <param name="PriorMessageSenderDisplayNames">
/// Optional pre-resolved map from prior-message sender <see cref="Address"/>
/// to a human-readable display name (#2129). When supplied,
/// <c>ThreadContextBuilder</c> renders prior turns as
/// <c>[ts] {DisplayName}: …</c> instead of leaking the raw
/// <c>scheme://&lt;guid&gt;</c> wire form into Layer 3 — that wire shape
/// is what weak LLMs were observed to mimic on output (see #2089). Built
/// upstream by the actor (which has scoped access to
/// <see cref="Cvoya.Spring.Core.Security.IParticipantDisplayNameResolver"/>)
/// so the singleton prompt-assembly path stays free of scoped
/// dependencies. When the map is <c>null</c> or omits an address,
/// <c>ThreadContextBuilder</c> falls back to the address's scheme literal
/// (e.g. <c>human</c> / <c>agent</c>) — never the raw GUID.
/// </param>
/// <param name="ConnectorPromptFragments">
/// Optional ordered list of markdown fragments contributed by each
/// connector binding applicable to the launch subject (#2442). The
/// assembler concatenates these into a platform-layer subsection
/// titled "Connector context (auto-injected by platform)" — telling
/// the agent what env-vars its container has, what bound resource
/// identity each binding represents, and how to use the connector's
/// CLI tools. Built upstream by the dispatcher via
/// <c>IConnectorPromptContextResolver</c>; an empty / null list omits
/// the subsection entirely.
/// </param>
/// <remarks>
/// The peer-directory member list that used to live on this record was
/// removed in #2231 once the runtime gained the <c>sv.*</c> directory
/// tools — composition is now an on-demand tool query, not a prompt
/// layer. Anything that needs the peer set should call
/// <c>sv.list_members</c> at runtime instead of relying on a
/// prompt-time render.
/// </remarks>
public record PromptAssemblyContext(
    JsonElement? Policies,
    IReadOnlyList<Skill>? Skills,
    IReadOnlyList<Message> PriorMessages,
    string? LastCheckpoint,
    string? AgentInstructions,
    AgentMetadata? EffectiveMetadata = null,
    IReadOnlyList<SkillBundle>? SkillBundles = null,
    IReadOnlyList<SkillBundle>? AgentSkillBundles = null,
    IReadOnlyList<PendingAmendment>? PendingAmendments = null,
    IReadOnlyDictionary<Address, string>? PriorMessageSenderDisplayNames = null,
    IReadOnlyList<string>? ConnectorPromptFragments = null);
