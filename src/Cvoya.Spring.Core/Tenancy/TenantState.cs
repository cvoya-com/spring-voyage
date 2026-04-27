// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Lifecycle state of a <see cref="TenantRecord"/>.
/// </summary>
/// <remarks>
/// v0.1 keeps the surface deliberately small — every active tenant is
/// <see cref="Active"/>; <see cref="Deleted"/> covers the soft-delete row
/// the registry retains after a DELETE so an audit log of past tenants
/// is still queryable. Suspended / archived states are intentionally
/// out of scope; the cloud overlay will introduce additional values via
/// downstream extension when the use cases become concrete.
/// </remarks>
public enum TenantState
{
    /// <summary>The tenant is live and can be referenced by tenant-scoped data.</summary>
    Active = 0,

    /// <summary>
    /// The tenant has been soft-deleted. Tenant-scoped data is retained for
    /// audit / restoration but the registry will not surface this row in
    /// the default list view.
    /// </summary>
    Deleted = 1,
}