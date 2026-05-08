// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Orchestration;

/// <summary>
/// Read/write seam for the persisted <c>orchestration.strategy</c> key that
/// drives <see cref="IOrchestrationStrategyResolver"/> (#606). The dedicated
/// HTTP surface (<c>PUT /api/v1/units/{id}/orchestration</c>) writes through
/// this interface so persistence shape, cache invalidation, and validation
/// semantics stay centralised.
/// </summary>
/// <remarks>
/// <para>
/// Implementations are expected to persist into the same
/// <c>UnitDefinitions.Definition</c> JSON document the
/// <see cref="IOrchestrationStrategyProvider"/> reads from — that document
/// is the single source of declarative truth for the per-message resolver
/// described in ADR-0010. Implementations must invoke
/// <see cref="IOrchestrationStrategyCacheInvalidator.Invalidate(string)"/>
/// on successful writes so in-process resolver caches see the change
/// immediately instead of waiting for the TTL to expire.
/// </para>
/// <para>
/// Implementations take the user-facing unit name (address path / unique
/// identifier) — not the Dapr actor id — because the dedicated HTTP surface
/// addresses units by name. Implementations are free to translate to the
/// actor id internally when reading back through the strategy-provider
/// seam for cache invalidation.
/// </para>
/// </remarks>
public interface IUnitOrchestrationStore
{
    /// <summary>
    /// Returns the persisted <c>orchestration.strategy</c> key for the
    /// given unit or <c>null</c> when no key has been declared. Never
    /// throws for a missing definition row — a unit whose manifest omitted
    /// the block returns <c>null</c> so callers can surface the "inferred
    /// / default" state without branching on 404.
    /// </summary>
    /// <param name="unitId">The user-facing unit name / address path.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<string?> GetStrategyKeyAsync(
        string unitId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes the <c>orchestration.strategy</c> key in place on the unit's
    /// persisted definition JSON. A <c>null</c> / whitespace key is an
    /// explicit clear — the orchestration block is removed so the resolver
    /// falls back to policy inference / the unkeyed default (ADR-0010).
    /// Implementations must preserve every other property on the Definition
    /// document (expertise, instructions, execution…) and fire the
    /// configured <see cref="IOrchestrationStrategyCacheInvalidator"/> on
    /// success.
    /// </summary>
    /// <param name="unitId">The user-facing unit name / address path.</param>
    /// <param name="strategyKey">
    /// The DI key naming the <see cref="IOrchestrationStrategy"/>
    /// implementation this unit should resolve on every message. Expected
    /// platform-offered values today: <c>ai</c>, <c>workflow</c>,
    /// <c>label-routed</c>. Hosts registering additional strategies via
    /// <c>AddKeyedScoped&lt;IOrchestrationStrategy, ...&gt;</c> can accept
    /// their keys here too — the store does not whitelist (the resolver
    /// degrades to the default on unknown keys per ADR-0010).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task SetStrategyKeyAsync(
        string unitId,
        string? strategyKey,
        CancellationToken cancellationToken = default);
}