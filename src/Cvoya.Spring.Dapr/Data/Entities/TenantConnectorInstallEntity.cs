// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using System.Text.Json;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Row in <c>tenant_connector_installs</c> — records that a given tenant
/// has a given <see cref="Cvoya.Spring.Connectors.IConnectorType"/> installed,
/// together with tenant-scoped configuration. Distinct from
/// <see cref="ConnectorDefinitionEntity"/> (per-unit binding) — this row
/// lives one level higher and controls which connectors are AVAILABLE on
/// the tenant at all.
/// </summary>
public class TenantConnectorInstallEntity : ITenantScopedEntity
{
    /// <summary>Tenant that owns this install row.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>
    /// Stable connector identifier matching
    /// <see cref="Cvoya.Spring.Connectors.IConnectorType.Slug"/>. Slugs are
    /// URL-safe kebab-case strings (e.g. <c>github</c>, <c>arxiv</c>).
    /// </summary>
    public string ConnectorId { get; set; } = string.Empty;

    /// <summary>
    /// Opaque tenant-scoped config payload. Connectors that need tenant-wide
    /// defaults (base URLs, shared limits) store them here. <c>null</c> for
    /// connectors that don't carry tenant-level configuration.
    /// </summary>
    public JsonElement? ConfigJson { get; set; }

    /// <summary>Timestamp when the connector was first installed on the tenant.</summary>
    public DateTimeOffset InstalledAt { get; set; }

    /// <summary>Timestamp when the install row was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Soft-delete marker — non-null rows are treated as uninstalled.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}