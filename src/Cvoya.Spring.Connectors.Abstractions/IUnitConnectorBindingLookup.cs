// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connectors;

/// <summary>
/// Connector-agnostic read seam for "find me every binding of this
/// connector type". Connector packages need this when an inbound delivery
/// from their external system arrives without a unit reference and the
/// connector must resolve the target unit from coordinates carried in
/// the payload (e.g. GitHub's <c>(installation_id, owner, repo)</c>).
/// </summary>
/// <remarks>
/// <para>
/// Issue #2456 — App-level delivery only. GitHub no longer registers
/// per-repo webhooks, so an inbound webhook is not pre-routed to a
/// specific unit; the connector enumerates its own bindings and matches
/// the payload coordinates against each binding's typed config. The
/// abstraction stays connector-agnostic: the platform exposes
/// "list bindings of this type" and the connector owns the
/// payload-to-config match logic.
/// </para>
/// <para>
/// Hosts register a singleton implementation (the OSS default is backed
/// by the EF binding repository through a per-call DI scope, mirroring
/// <see cref="IUnitConnectorConfigStore"/>). Cloud overlays can layer
/// audit logging or tenant-scope guards on top via TryAdd composition.
/// </para>
/// </remarks>
public interface IUnitConnectorBindingLookup
{
    /// <summary>
    /// Returns every active binding for <paramref name="connectorTypeId"/>
    /// in the current tenant scope, paired with the owning unit's
    /// canonical hex id (the 32-char N-format Guid string that
    /// <c>Address.For("unit", id)</c> consumes).
    /// </summary>
    /// <param name="connectorTypeId">
    /// The connector type id from <see cref="IConnectorType.TypeId"/>.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// All bindings whose connector-type id equals
    /// <paramref name="connectorTypeId"/>. Returns an empty list when no
    /// unit is bound to that connector type.
    /// </returns>
    Task<IReadOnlyList<UnitConnectorBindingEntry>> ListByConnectorTypeAsync(
        Guid connectorTypeId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One row returned by <see cref="IUnitConnectorBindingLookup.ListByConnectorTypeAsync"/>.
/// </summary>
/// <param name="UnitId">
/// The owning unit's canonical hex id (32-char N-format Guid string), in
/// the shape <c>Address.For("unit", id)</c> consumes. Connectors
/// re-address translated messages to <c>unit://{UnitId}</c>.
/// </param>
/// <param name="Binding">
/// The persisted binding payload (connector-type id + typed config).
/// </param>
public sealed record UnitConnectorBindingEntry(string UnitId, UnitConnectorBinding Binding);
