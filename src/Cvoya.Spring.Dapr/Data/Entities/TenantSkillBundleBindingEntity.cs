// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Row in <c>tenant_skill_bundle_bindings</c> — records that a tenant
/// has opted into a given skill bundle. Enforced at resolve-time by the
/// tenant-filtering decorator around
/// <see cref="Cvoya.Spring.Core.Skills.ISkillBundleResolver"/>.
/// </summary>
public class TenantSkillBundleBindingEntity : ITenantScopedEntity
{
    /// <summary>Tenant that owns this binding.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Package directory name under the configured packages root
    /// (<c>Skills:PackagesRoot</c> or the shared <c>Packages:Root</c>).</summary>
    public string BundleId { get; set; } = string.Empty;

    /// <summary>
    /// Whether the bundle is visible to the tenant. Disabled rows are
    /// retained so a later rebind preserves <see cref="BoundAt"/>.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>Timestamp when the binding was first created.</summary>
    public DateTimeOffset BoundAt { get; set; }

    /// <summary>
    /// Package install lifecycle state (ADR-0035 decision 11).
    /// Rows written by the legacy path default to <see cref="PackageInstallState.Active"/>.
    /// </summary>
    public PackageInstallState InstallState { get; set; } = PackageInstallState.Active;

    /// <summary>
    /// FK to <see cref="PackageInstallEntity.InstallId"/>. <c>null</c> for rows
    /// written by the legacy path.
    /// </summary>
    public Guid? InstallId { get; set; }
}