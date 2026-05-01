// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persists one (unit, agent) membership edge with per-membership config
/// overrides. Replaces the single-pointer <c>Agent:ParentUnit</c> state
/// at the actor layer with an M:N join row (see #160 / C2b). Unit-typed
/// members remain 1:N and are not stored here — only <c>agent://</c>
/// members have rows in this table.
///
/// As of #1492 the primary key columns are stable UUIDs (actor IDs),
/// not slug strings. A delete + recreate of a unit or agent with the
/// same slug no longer inherits stale membership rows from the prior
/// instance.
/// </summary>
public class UnitMembershipEntity : ITenantScopedEntity
{
    /// <summary>Gets or sets the tenant that owns this membership row.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Stable UUID identity of the unit this membership attaches the agent
    /// to. Matches the unit's <c>ActorId</c> so a delete + recreate of a
    /// unit with the same slug does not inherit stale rows (#1492).
    /// </summary>
    public Guid UnitId { get; set; }

    /// <summary>
    /// Stable UUID identity of the agent. Matches the agent's <c>ActorId</c>.
    /// Renamed from the prior <c>AgentAddress</c> column because "address"
    /// implied a slug-shaped URI string (#1492).
    /// </summary>
    public Guid AgentId { get; set; }

    /// <summary>Optional per-membership model override.</summary>
    public string? Model { get; set; }

    /// <summary>Optional per-membership specialty override.</summary>
    public string? Specialty { get; set; }

    /// <summary>
    /// Per-membership enabled flag. Defaults to <c>true</c> on insert.
    /// A <c>false</c> value causes the unit's orchestration strategy to
    /// skip this agent; other units the agent belongs to are unaffected.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Optional per-membership execution-mode override.</summary>
    public AgentExecutionMode? ExecutionMode { get; set; }

    /// <summary>UTC timestamp when the membership was first created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC timestamp when the membership was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Marks this membership as the agent's primary parent unit. Exactly
    /// one row per <c>(tenant_id, agent_id)</c> carries <c>true</c>;
    /// the repository auto-assigns on first insert and auto-promotes when
    /// the primary row is deleted.
    /// </summary>
    public bool IsPrimary { get; set; }
}