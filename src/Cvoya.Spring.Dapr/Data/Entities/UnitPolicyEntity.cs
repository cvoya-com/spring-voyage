// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using System.Text.Json;

/// <summary>
/// Persisted <see cref="Core.Policies.UnitPolicy"/> for a unit. One row per
/// unit that has at least one non-empty policy dimension. The sibling-entity
/// shape (rather than a column on <see cref="UnitDefinitionEntity"/>) keeps
/// policy writes independent of unit-definition writes and lets the policy
/// record grow over time (model caps, cost caps, execution-mode, initiative)
/// without churning the <c>unit_definitions</c> schema.
/// </summary>
public class UnitPolicyEntity
{
    /// <summary>
    /// The unit id (<c>Address.Path</c>). Primary key — at most one policy
    /// row per unit.
    /// </summary>
    public string UnitId { get; set; } = string.Empty;

    /// <summary>
    /// Persisted skill policy encoded as JSON, or <c>null</c> when the unit
    /// does not constrain skill invocation. Stored as <c>jsonb</c> on
    /// PostgreSQL so future dimensions do not require DDL changes.
    /// </summary>
    public JsonElement? Skill { get; set; }

    /// <summary>
    /// Timestamp when the row was first inserted.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Timestamp when the row was last updated. Stamped by
    /// <see cref="SpringDbContext"/>'s audit hook on every write.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}