// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Marker contract implemented by every business-data entity that must
/// be scoped to a tenant. Presence of this interface is the convention
/// that pairs with an EF Core query filter of the form
/// <c>HasQueryFilter(e =&gt; e.TenantId == tenantContext.CurrentTenantId
/// &amp;&amp; e.DeletedAt == null)</c> on the corresponding
/// <c>IEntityTypeConfiguration</c>. The filter is enforced
/// platform-wide so ad-hoc <c>DbSet</c> queries cannot accidentally
/// leak rows across tenants.
///
/// <para>
/// System / ops tables (migrations history, startup configuration) do
/// <b>not</b> implement this interface — they are intentionally global
/// and remain outside the tenant query filter. When in doubt: a row
/// that a customer could observe or mutate is business-data and must
/// be tenant-scoped.
/// </para>
///
/// <para>
/// When a new business-data entity is introduced, the implementer must
/// (a) implement this interface, (b) set <see cref="TenantId"/> on
/// insert paths to the current <c>ITenantContext.CurrentTenantId</c>,
/// and (c) compose the tenant filter with any existing soft-delete
/// filter in the entity's configuration. See
/// <c>CONVENTIONS.md</c> § Multi-tenancy.
/// </para>
/// </summary>
public interface ITenantScopedEntity
{
    /// <summary>
    /// Tenant identifier that owns this row. Never null or empty on
    /// persisted rows. Populated from the ambient
    /// <see cref="ITenantContext.CurrentTenantId"/> on insert and
    /// preserved through updates; the column is NOT NULL at the
    /// database layer.
    /// </summary>
    string TenantId { get; }
}