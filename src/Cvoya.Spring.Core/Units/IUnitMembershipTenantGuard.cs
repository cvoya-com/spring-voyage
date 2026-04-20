// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Domain service that enforces the "same tenant for both sides"
/// invariant on every composition-edge write (#745).
/// <para>
/// The default implementation consults the tenant-scoped entity tables
/// for the unit / agent so lookups automatically honour the ambient
/// <see cref="Tenancy.ITenantContext"/>. Callers use the guard in write
/// paths (add-agent-to-unit, add-unit-to-unit, create-agent) so a
/// single seam prevents sprinkled, drift-prone checks across endpoints
/// and services.
/// </para>
/// <para>
/// Read paths (hierarchy walkers, expertise aggregation) call
/// <see cref="ShareTenantAsync(Address, Address, System.Threading.CancellationToken)"/>
/// to filter cross-tenant edges defensively — corrupted actor-state
/// cannot leak routing across tenants even if a write slipped through.
/// </para>
/// </summary>
public interface IUnitMembershipTenantGuard
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="parent"/> and
    /// <paramref name="member"/> live in the same tenant. Returns
    /// <c>false</c> when either side is unknown in the current tenant
    /// (the tenant filter applied to the lookup hides other-tenant rows)
    /// or when their persisted tenants explicitly differ. Never throws
    /// on unknown addresses — the read-path defensive filter prefers
    /// "not the same tenant" over failing the whole walk.
    /// </summary>
    /// <param name="parent">The owning <c>unit://</c> address.</param>
    /// <param name="member">The <c>agent://</c> or <c>unit://</c> address being inspected.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task<bool> ShareTenantAsync(Address parent, Address member, CancellationToken cancellationToken = default);

    /// <summary>
    /// Throws <see cref="CrossTenantMembershipException"/> when
    /// <paramref name="parent"/> and <paramref name="member"/> do not
    /// live in the same tenant. Write paths call this before recording
    /// the composition edge so the invariant is enforced in one place.
    /// Unknown addresses (either side missing in the current tenant) are
    /// treated as cross-tenant and rejected, matching the "do not leak
    /// other-tenant existence" response shape.
    /// </summary>
    Task EnsureSameTenantAsync(Address parent, Address member, CancellationToken cancellationToken = default);
}