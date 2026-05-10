// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using System.Text.Json;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persists the durable connector binding for a single unit. 1:1 with
/// <see cref="UnitDefinitionEntity"/>: at most one row per
/// <c>(tenant_id, unit_id)</c> at any time. Replaces the actor-state
/// <c>Unit:ConnectorBinding</c> + <c>Unit:ConnectorMetadata</c> pair so
/// the binding survives actor restarts and so the connector lifecycle
/// hooks (start / stop) can be answered from a single SQL read on the
/// API host without having to round-trip the unit actor.
///
/// <para>
/// Implements ADR-0040 / #2050. <c>Connector:Status</c>,
/// <c>Connector:Type</c>, and <c>Connector:Config</c> remain
/// instance-local runtime state on <c>ConnectorActor</c> per ADR-0040 —
/// they are connector-instance facts, rebuilt from this row at
/// activation time.
/// </para>
/// </summary>
public class UnitConnectorBindingEntity : ITenantScopedEntity
{
    /// <summary>Synthetic primary key.</summary>
    public Guid Id { get; set; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>
    /// Stable Guid identity of the unit this binding attaches to. Together
    /// with <see cref="TenantId"/> this column carries the unique index
    /// that enforces "at most one connector binding per unit" — when the
    /// operator rebinds the unit to a different connector, the existing
    /// row is updated in place.
    /// </summary>
    public Guid UnitId { get; set; }

    /// <summary>
    /// The connector type id (matching
    /// <c>Cvoya.Spring.Connectors.IConnectorType.TypeId</c>) the unit is
    /// bound to. Identifies which package owns the binding without forcing
    /// a foreign key against a connector-types table — the registry of
    /// connector types is in-process via DI, not relational.
    /// </summary>
    public Guid ConnectorType { get; set; }

    /// <summary>
    /// Connector-specific typed config payload, opaque to the platform.
    /// The concrete shape is owned by the connector type identified by
    /// <see cref="ConnectorType"/>; the platform never deserialises it.
    /// Stored as <c>jsonb</c> so the column can hold heterogeneous shapes
    /// across connector types without DDL changes.
    /// </summary>
    public JsonElement Config { get; set; }

    /// <summary>
    /// Connector-owned runtime metadata persisted on a unit — e.g. a
    /// GitHub webhook id created at <c>/start</c> and required by
    /// <c>/stop</c> to call
    /// <c>DELETE /repos/{owner}/{repo}/hooks/{id}</c>. The platform
    /// treats this as opaque <c>jsonb</c>; only the owning connector
    /// reads or writes the inner shape. <c>null</c> when the connector
    /// has not stored any runtime metadata yet.
    /// </summary>
    public JsonElement? Metadata { get; set; }

    /// <summary>
    /// UTC timestamp of when the binding was first created. Re-binds to
    /// a different connector type update this column so the audit trail
    /// matches the active row's lifetime.
    /// </summary>
    public DateTimeOffset BoundAt { get; set; }
}
