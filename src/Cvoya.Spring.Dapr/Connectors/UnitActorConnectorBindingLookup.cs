// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Connectors;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Identifiers;

/// <summary>
/// Default <see cref="IUnitConnectorBindingLookup"/> implementation. Per
/// ADR-0040 / #2050 connector bindings live in
/// <c>unit_connector_bindings</c>; this adapter forwards every call to
/// <see cref="IUnitConnectorBindingStore.ListByConnectorTypeAsync"/> and
/// returns the unit id in the 32-char N-format hex shape connector
/// packages re-address messages against (#2456).
/// </summary>
public class UnitActorConnectorBindingLookup(
    IUnitConnectorBindingStore bindingStore) : IUnitConnectorBindingLookup
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<UnitConnectorBindingEntry>> ListByConnectorTypeAsync(
        Guid connectorTypeId, CancellationToken cancellationToken = default)
    {
        var rows = await bindingStore.ListByConnectorTypeAsync(connectorTypeId, cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        var result = new List<UnitConnectorBindingEntry>(rows.Count);
        foreach (var row in rows)
        {
            result.Add(new UnitConnectorBindingEntry(
                GuidFormatter.Format(row.UnitId),
                row.Binding));
        }
        return result;
    }
}
