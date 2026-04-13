// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Agents;

/// <summary>
/// Mutable per-agent configuration, owned by the agent itself. Mirrors
/// <see cref="Units.UnitMetadata"/>: all fields are optional, and consumers
/// of a set / patch operation treat <c>null</c> as "leave the existing
/// state untouched" so partial PATCH requests behave correctly.
/// </summary>
/// <param name="Model">Preferred LLM model identifier for this agent, or <c>null</c> to inherit.</param>
/// <param name="Specialty">Free-form label describing this agent's role (e.g., "reviewer", "implementer"). Used by orchestration strategies that pick agents by specialty.</param>
/// <param name="Enabled">When <c>false</c>, orchestration strategies skip this agent. Re-enabling is cheap.</param>
/// <param name="ExecutionMode">How this agent participates in message dispatch.</param>
/// <param name="ParentUnit">
/// The unit this agent belongs to. Maintained by the unit's assign / unassign
/// endpoints alongside the unit's <c>members</c> list so the two stay in sync.
/// <b>Not editable via agent PATCH</b> — changing containment must go through
/// the assign flow to enforce the 1:N invariant (an agent belongs to at most
/// one unit; see <c>#160</c> for the M:N future consideration).
/// </param>
public record AgentMetadata(
    string? Model = null,
    string? Specialty = null,
    bool? Enabled = null,
    AgentExecutionMode? ExecutionMode = null,
    string? ParentUnit = null);