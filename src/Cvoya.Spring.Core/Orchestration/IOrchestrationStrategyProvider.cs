// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Orchestration;

/// <summary>
/// Resolves the declarative orchestration-strategy key declared on a unit's
/// persisted <c>UnitDefinition</c> YAML (the <c>orchestration.strategy:</c>
/// block — see #491). Consumed by <c>UnitActor.HandleDomainMessageAsync</c>
/// (via <see cref="IOrchestrationStrategyResolver"/>) so the right keyed
/// <see cref="IOrchestrationStrategy"/> implementation is resolved at
/// dispatch time instead of always binding to the unkeyed default.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors the shape of <c>IExpertiseSeedProvider</c>: the implementation
/// reads the same persisted definition JSON rows (<c>UnitDefinitions.Definition</c>)
/// and surfaces a single declarative slot. Kept as a dedicated interface so
/// the private cloud host can swap in a tenant-scoped reader (or a hot-path
/// cache) without forking the OSS default.
/// </para>
/// <para>
/// Implementations must return <c>null</c> (not an empty string) when no
/// <c>orchestration.strategy</c> block is declared on the unit so the
/// caller can distinguish "no manifest directive" — which triggers the
/// policy-based fallback (`UnitPolicy.LabelRouting` → `label-routed`) and
/// then the unkeyed platform default — from "declared as ...". Implementations
/// must not throw for missing units — a missing definition behaves the same
/// as a missing slot.
/// </para>
/// </remarks>
public interface IOrchestrationStrategyProvider
{
    /// <summary>
    /// Reads the orchestration-strategy key declared for the given unit id.
    /// Returns <c>null</c> when no definition row exists or the YAML had no
    /// <c>orchestration.strategy:</c> block; returns the raw string (the DI
    /// key) when one was declared.
    /// </summary>
    /// <param name="unitId">
    /// The unit identifier. Implementations should accept either the
    /// user-facing unit name (<c>UnitDefinitionEntity.UnitId</c>) or the
    /// Dapr actor id (<c>UnitDefinitionEntity.ActorId</c>) — actor
    /// activation passes <c>Id.GetId()</c> (the actor GUID) but tests and
    /// CLI callers pass the user-facing name.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<string?> GetStrategyKeyAsync(
        string unitId,
        CancellationToken cancellationToken = default);
}