// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Orchestration;

/// <summary>
/// Optional invalidation hook consumed by known-write paths (notably
/// <c>UnitCreationService.PersistUnitDefinitionOrchestrationAsync</c>) that
/// mutate the <c>orchestration.strategy</c> slot on a unit's persisted
/// definition. When a caching <see cref="IOrchestrationStrategyProvider"/>
/// decorator is registered it implements this interface and drops the
/// cached entry the moment the write commits, so the next message dispatched
/// to the unit sees the new strategy without waiting for the cache TTL to
/// expire (see #518).
/// </summary>
/// <remarks>
/// <para>
/// Write-path callers resolve this through DI as optional — the non-caching
/// provider path registers a no-op implementation so call sites never branch
/// on null. Third-party <see cref="IOrchestrationStrategyProvider"/>
/// implementations that don't cache can register <c>NullOrchestrationStrategyCacheInvalidator.Instance</c>
/// (or simply let the default registration ride).
/// </para>
/// <para>
/// The contract is fire-and-forget: invalidation must never throw even if
/// the cache is cold or the unit id was never cached. Implementations are
/// free to also accept a sentinel "invalidate everything" call via
/// <see cref="InvalidateAll"/> for bulk operations like reapplying a batch
/// of manifests.
/// </para>
/// </remarks>
public interface IOrchestrationStrategyCacheInvalidator
{
    /// <summary>
    /// Drops the cached strategy key for the given unit id, if any. A
    /// unitId that was never cached (or never existed) is a no-op.
    /// </summary>
    /// <param name="unitId">The unit identifier whose cache entry should be dropped.</param>
    void Invalidate(string unitId);

    /// <summary>
    /// Drops every cached entry. Intended for bulk operations (batch
    /// manifest apply, admin reset) rather than per-message use.
    /// </summary>
    void InvalidateAll();
}