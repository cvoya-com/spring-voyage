// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Policies;

/// <summary>
/// Persistence abstraction for <see cref="UnitPolicy"/> values keyed by unit
/// id. Kept in <c>Cvoya.Spring.Core</c> so the private cloud repo can supply
/// a tenant-scoped wrapper via DI without taking a dependency on
/// <c>Cvoya.Spring.Dapr</c>. The default implementation stores rows in a
/// sibling <c>unit_policies</c> table — chosen over a column on
/// <c>unit_definitions</c> so the policy shape can grow over time without
/// schema churn on the unit table, and so policy writes do not contend with
/// unit-definition writes.
/// </summary>
public interface IUnitPolicyRepository
{
    /// <summary>
    /// Returns the persisted policy for the unit, or <see cref="UnitPolicy.Empty"/>
    /// when no row exists. Callers never have to branch on "is there a row" —
    /// an empty policy is semantically identical to "no row".
    /// </summary>
    /// <param name="unitId">The unit identifier (<c>Address.Path</c>).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The persisted policy, or an empty policy if none.</returns>
    Task<UnitPolicy> GetAsync(string unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the policy for the unit. Passing <see cref="UnitPolicy.Empty"/>
    /// is a valid "clear all constraints" operation and persists a row with
    /// every dimension set to <c>null</c>. The repository is free to represent
    /// that as a delete so the row count reflects units that actually have a
    /// policy.
    /// </summary>
    /// <param name="unitId">The unit identifier (<c>Address.Path</c>).</param>
    /// <param name="policy">The policy to persist.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task SetAsync(string unitId, UnitPolicy policy, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the policy row for the unit. No-op when no row exists.
    /// Called by unit-delete flows so orphan policy rows are not left behind.
    /// </summary>
    /// <param name="unitId">The unit identifier (<c>Address.Path</c>).</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task DeleteAsync(string unitId, CancellationToken cancellationToken = default);
}