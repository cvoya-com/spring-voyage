// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using System.Text.Json;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persists a per-tenant connector binding (ADR-0061 §1). At most one row
/// per <c>(tenant_id, connector_slug)</c> at any time. Parallels
/// <see cref="UnitConnectorBindingEntity"/>: the per-unit binding table
/// (<c>unit_connector_bindings</c>) carries connectors whose scope is the
/// unit; this table (<c>tenant_connector_bindings</c>) carries connectors
/// whose scope is the tenant — currently the Slack connector, and any
/// future workspace-shaped connector (calendar, shared mailbox, ...).
///
/// <para>
/// The row is keyed by <c>(tenant_id, connector_slug)</c> rather than by
/// connector type id. The slug is stable across releases (it is
/// URL-visible and persisted in audit logs) and lets the repository
/// dispatch without re-resolving connector types from DI on every read.
/// </para>
///
/// <para>
/// The table is connector-agnostic per ADR-0061 §7.7: the
/// <see cref="Config"/> column is opaque <c>jsonb</c> the platform never
/// deserialises. Each connector owns the shape of its own payload.
/// </para>
/// </summary>
public class TenantConnectorBindingEntity : ITenantScopedEntity
{
    /// <summary>Synthetic primary key.</summary>
    public Guid Id { get; set; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>
    /// The connector slug (matches
    /// <c>Cvoya.Spring.Connectors.IConnectorType.Slug</c>) the tenant is
    /// bound to. Together with <see cref="TenantId"/> this column carries
    /// the unique index that enforces "at most one binding per tenant per
    /// connector slug" — when the operator rebinds the same tenant to a
    /// fresh install of the same connector, the existing row is updated
    /// in place.
    /// </summary>
    public string ConnectorSlug { get; set; } = string.Empty;

    /// <summary>
    /// Stable connector-type id (matches
    /// <c>Cvoya.Spring.Connectors.IConnectorType.TypeId</c>). Stored
    /// alongside the slug so a future slug rename does not orphan
    /// existing rows.
    /// </summary>
    public Guid ConnectorType { get; set; }

    /// <summary>
    /// Connector-specific typed config payload, opaque to the platform.
    /// The concrete shape is owned by the connector identified by
    /// <see cref="ConnectorSlug"/> / <see cref="ConnectorType"/>; the
    /// platform never deserialises it. Stored as <c>jsonb</c> so the
    /// column can hold heterogeneous shapes across connector types
    /// without DDL changes (ADR-0061 §7.7).
    /// </summary>
    public JsonElement Config { get; set; }

    /// <summary>
    /// Connector-owned runtime metadata persisted on a tenant binding —
    /// the per-tenant equivalent of
    /// <see cref="UnitConnectorBindingEntity.Metadata"/>. The platform
    /// treats this as opaque <c>jsonb</c>; only the owning connector
    /// reads or writes the inner shape. <c>null</c> when the connector
    /// has not stored any runtime metadata yet.
    /// </summary>
    public JsonElement? Metadata { get; set; }

    /// <summary>
    /// UTC timestamp of when the binding was first created. Re-binds to
    /// the same slug update this column so the audit trail matches the
    /// active row's lifetime.
    /// </summary>
    public DateTimeOffset BoundAt { get; set; }

    /// <summary>
    /// Connector-native identifier of the external resource the binding
    /// addresses (e.g. the Slack <c>team_id</c>). Used by inbound-
    /// webhook routing to resolve a delivery to a tenant when the
    /// delivery only carries the external identifier. <c>null</c> for
    /// connectors that do not surface one.
    ///
    /// <para>
    /// The platform applies a UNIQUE index on
    /// <c>(connector_slug, external_identity)</c> (where
    /// <c>external_identity IS NOT NULL</c>) — the same external
    /// resource cannot be claimed by two tenants. This preserves the
    /// pre-cleanup <c>tenant_slack_workspace_map</c> invariant on the
    /// binding row itself.
    /// </para>
    /// </summary>
    public string? ExternalIdentity { get; set; }
}
