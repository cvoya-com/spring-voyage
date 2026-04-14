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
/// <param name="Members">The addresses of peer agents in the unit.</param>
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
/// Optional ordered list of package-level skill bundles resolved from the
/// unit manifest (see #167). Each bundle contributes a prompt fragment and a
/// list of required tools. Prompts are concatenated in declaration order and
/// rendered as a sub-section of Layer 2 (unit context) so the ordering is:
/// platform → unit context (including bundle prompts) → conversation →
/// agent instructions. The surrounding layer order matches the existing
/// four-layer assembly; bundle prompts are additive and never interleave
/// with agent-specific instructions.
/// </param>
public record PromptAssemblyContext(
    IReadOnlyList<Address> Members,
    JsonElement? Policies,
    IReadOnlyList<Skill>? Skills,
    IReadOnlyList<Message> PriorMessages,
    string? LastCheckpoint,
    string? AgentInstructions,
    AgentMetadata? EffectiveMetadata = null,
    IReadOnlyList<SkillBundle>? SkillBundles = null);