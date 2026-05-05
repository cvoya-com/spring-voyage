// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using System.Text.Json;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Row in <c>tenant_connector_installs</c>. Three logical "scopes" share
/// the same table after #1671 (the connector-binding chain):
/// <list type="bullet">
///   <item><description>
///     <b>Tenant-level</b> (both <see cref="PackageInstallId"/> and
///     <see cref="UnitId"/> null) — the legacy semantic: tenant has the
///     connector installed at all (visible in the wizard / CLI).
///   </description></item>
///   <item><description>
///     <b>Package-scope binding</b> (<see cref="PackageInstallId"/> set,
///     <see cref="UnitId"/> null) — the binding the operator supplied at
///     install time, inherited by every member unit unless overridden.
///   </description></item>
///   <item><description>
///     <b>Unit-scope binding</b> (<see cref="UnitId"/> set) — per-unit
///     override, takes precedence over the package-scope binding.
///   </description></item>
/// </list>
/// Distinct from <see cref="ConnectorDefinitionEntity"/> (the per-unit
/// directory entry).
/// </summary>
public class TenantConnectorInstallEntity : ITenantScopedEntity
{
    /// <summary>
    /// Synthetic primary key. Introduced in the connector-binding chain
    /// (#1671) so the same <c>(tenant_id, connector_id)</c> tuple can carry
    /// multiple rows (one tenant-level + one per package install + one per
    /// unit override). Existing tenant-level rows seeded by bootstrap get
    /// a fresh Guid on migration.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>Tenant that owns this install row.</summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Stable connector identifier matching
    /// <see cref="Cvoya.Spring.Connectors.IConnectorType.Slug"/>. Slugs are
    /// URL-safe kebab-case strings (e.g. <c>github</c>, <c>arxiv</c>).
    /// </summary>
    public string ConnectorId { get; set; } = string.Empty;

    /// <summary>
    /// Opaque config payload. For tenant-level rows: tenant-wide defaults.
    /// For package-scope and unit-scope rows: the operator-supplied
    /// <c>ConnectorBinding.Config</c> the install pipeline forwarded
    /// through.
    /// </summary>
    public JsonElement? ConfigJson { get; set; }

    /// <summary>Timestamp when the connector was first installed on the tenant.</summary>
    public DateTimeOffset InstalledAt { get; set; }

    /// <summary>Timestamp when the install row was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Soft-delete marker — non-null rows are treated as uninstalled.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// FK to <see cref="PackageInstallEntity.InstallId"/>. <c>null</c> for
    /// tenant-level rows; non-null for package-scope and unit-scope binding
    /// rows so they can be torn down when the install is aborted (#1671).
    /// </summary>
    public Guid? PackageInstallId { get; set; }

    /// <summary>
    /// FK to <see cref="UnitDefinitionEntity.Id"/>. <c>null</c> for
    /// tenant-level and package-scope rows. Non-null indicates a unit-scope
    /// override binding (#1671).
    /// </summary>
    public Guid? UnitId { get; set; }
}