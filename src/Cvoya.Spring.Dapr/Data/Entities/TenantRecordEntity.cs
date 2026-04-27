// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persistent shape of <see cref="TenantRecord"/> — the platform's
/// first-class tenants table that backs <c>/api/v1/platform/tenants</c>.
///
/// <para>
/// Deliberately NOT an <see cref="ITenantScopedEntity"/>: the registry
/// is global by nature. Cross-tenant reads of this table are legitimate
/// and the API gate enforces
/// <see cref="Cvoya.Spring.Core.Security.PlatformRoles.PlatformOperator"/>.
/// The DbContext therefore does not apply a tenant query filter to this
/// type — only the soft-delete clause excludes <c>deleted_at IS NOT NULL</c>
/// rows from the default view.
/// </para>
/// </summary>
public class TenantRecordEntity
{
    /// <summary>Stable tenant identifier; primary key.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-facing display name. Defaults to <see cref="Id"/> on create.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Lifecycle state — <see cref="TenantState.Active"/> or <see cref="TenantState.Deleted"/>.</summary>
    public TenantState State { get; set; }

    /// <summary>Creation timestamp.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Last-update timestamp.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>
    /// Soft-delete timestamp. <c>null</c> for active rows; set to the
    /// delete instant when <see cref="ITenantRegistry.DeleteAsync"/>
    /// removes the tenant.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; set; }
}