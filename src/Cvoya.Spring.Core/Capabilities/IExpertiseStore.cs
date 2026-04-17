// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Persistence seam for the per-entity expertise profile used by
/// <see cref="IExpertiseAggregator"/>. Keeping the store abstract (instead of
/// reading directly off actor state) lets the aggregator work uniformly for
/// agent-scheme and unit-scheme addresses, and lets the private cloud repo
/// swap in a tenant-scoped or observed store without changing the
/// aggregator.
/// </summary>
/// <remarks>
/// The default OSS implementation reads agent expertise from the
/// <c>AgentActor</c> and unit <em>own</em> expertise from the
/// <c>UnitActor</c>. Missing values return an empty list (not null) so
/// callers never have to null-check the iteration.
/// </remarks>
public interface IExpertiseStore
{
    /// <summary>
    /// Reads the configured expertise domains for the supplied entity
    /// address. Returns an empty list if the entity has no configured
    /// expertise or if the address does not resolve to a known entity.
    /// </summary>
    /// <param name="entity">An agent or unit address.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<IReadOnlyList<ExpertiseDomain>> GetDomainsAsync(
        Address entity,
        CancellationToken cancellationToken = default);
}