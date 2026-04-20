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
/// </summary>
public class UnitMembershipEntity : ITenantScopedEntity
{
    /// <summary>Gets or sets the tenant that owns this membership row.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The unit this membership attaches the agent to.</summary>
    public string UnitId { get; set; } = string.Empty;

    /// <summary>
    /// Canonical string form of the agent's address
    /// (<c>Address.Path</c> for <c>scheme=agent</c>). Stored as a string
    /// to avoid persisting the full two-tuple; the scheme is implied.
    /// </summary>
    public string AgentAddress { get; set; } = string.Empty;

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
}