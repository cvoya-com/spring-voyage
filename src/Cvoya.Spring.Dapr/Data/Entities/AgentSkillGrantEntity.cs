// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persists one (agent, skill) grant. Replaces the actor-state
/// <c>Agent:Skills</c> list with N tenant-scoped rows so skill grants
/// survive actor restarts and so authorization / orchestration can query
/// them with relational tools rather than rehydrating an actor.
///
/// <para>
/// Implements ADR-0040 / #2048 for the agent skill-grants slice.
/// Uniqueness is enforced on <c>(tenant_id, agent_id, skill_name)</c> via
/// a unique index — a skill can be granted at most once per agent per
/// tenant. <c>SetSkillsAsync</c> uses replace-in-full semantics; the row
/// set is recomputed on every set call.
/// </para>
/// </summary>
public class AgentSkillGrantEntity : ITenantScopedEntity
{
    /// <summary>Synthetic primary key for the row.</summary>
    public Guid Id { get; set; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Stable Guid identity of the agent the grant attaches to.</summary>
    public Guid AgentId { get; set; }

    /// <summary>
    /// Canonical skill / tool name the agent is allowed to invoke.
    /// Stored exactly as supplied by the normalisation pass in
    /// <c>IAgentLiveConfigStore.SetSkillsAsync</c> (trimmed; whitespace
    /// rejected).
    /// </summary>
    public string SkillName { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the grant was first created.</summary>
    public DateTimeOffset GrantedAt { get; set; }
}
