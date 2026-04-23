// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

/// <summary>
/// Singleton write surface used by <c>UnitActor</c> to project
/// <c>unit://</c>-scheme membership edges into the persistent
/// <c>unit_subunit_memberships</c> table. Wraps the scoped
/// <see cref="IUnitSubunitMembershipRepository"/> with an
/// <c>IServiceScopeFactory</c>-style scope so an actor (which is not
/// itself request-scoped) can write through to EF without leaking DI
/// scope plumbing into the actor type.
/// </summary>
/// <remarks>
/// <para>
/// The actor-state list inside <c>UnitActor</c> remains authoritative
/// for runtime dispatch and cycle detection. Failures to project are
/// logged and swallowed by the caller — a stale projection is recovered
/// on the next host start by the reconciliation hosted service that
/// walks the directory and replays each unit's actor-state member list.
/// </para>
/// <para>
/// Defined in <c>Cvoya.Spring.Core</c> so the cloud overlay can
/// register a tenant-aware decorator (audit logging, permission checks,
/// per-tenant context) without taking a dependency on
/// <c>Cvoya.Spring.Dapr</c>.
/// </para>
/// </remarks>
public interface IUnitSubunitMembershipProjector
{
    /// <summary>
    /// Persists the parent → child edge. Idempotent — safe to call
    /// repeatedly from the actor's add path and from the startup
    /// reconciliation service.
    /// </summary>
    Task ProjectAddAsync(string parentUnitId, string childUnitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the parent → child edge. Idempotent — a missing row is
    /// a no-op.
    /// </summary>
    Task ProjectRemoveAsync(string parentUnitId, string childUnitId, CancellationToken cancellationToken = default);
}