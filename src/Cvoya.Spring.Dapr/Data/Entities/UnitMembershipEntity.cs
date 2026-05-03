// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persists one (unit, agent) membership edge with per-membership config
/// overrides. The composite primary key is
/// <c>(tenant_id, unit_id, agent_id)</c> with both identity columns typed
/// as Guid; there is no slug column on this table.
/// </summary>
public class UnitMembershipEntity : ITenantScopedEntity
{
    /// <summary>Gets or sets the tenant that owns this membership row.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Stable Guid identity of the unit this membership attaches the agent to.</summary>
    public Guid UnitId { get; set; }

    /// <summary>Stable Guid identity of the agent.</summary>
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
