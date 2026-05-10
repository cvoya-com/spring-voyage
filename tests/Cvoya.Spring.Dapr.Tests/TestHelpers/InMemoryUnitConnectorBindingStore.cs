// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.TestHelpers;

using System.Collections.Concurrent;
using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Dapr.Connectors;

/// <summary>
/// In-memory test double for <see cref="IUnitConnectorBindingStore"/>.
/// Lets unit tests exercise the EF-backed connector-binding surface
/// without standing up a Postgres / Testcontainer. Cross-restart
/// behaviour is covered by the integration tests with a real
/// <c>SpringDbContext</c>.
/// </summary>
public class InMemoryUnitConnectorBindingStore : IUnitConnectorBindingStore
{
    /// <summary>
    /// One slot per unit. The slot is removed on
    /// <see cref="ClearAsync"/> so <c>GetAsync</c> returning
    /// <c>null</c> matches the EF "no row" behaviour.
    /// </summary>
    private readonly ConcurrentDictionary<Guid, BindingSlot> _slots = new();

    public Task<UnitConnectorBinding?> GetAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(
            _slots.TryGetValue(unitId, out var slot)
                ? new UnitConnectorBinding(slot.ConnectorType, slot.Config)
                : null);
    }

    public Task SetAsync(
        Guid unitId,
        Guid connectorTypeId,
        JsonElement config,
        CancellationToken cancellationToken = default)
    {
        // Mirror the EF repository: a re-bind wipes runtime metadata
        // because it belonged to the previous connector type.
        _slots[unitId] = new BindingSlot(connectorTypeId, config, Metadata: null);
        return Task.CompletedTask;
    }

    public Task ClearAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        _slots.TryRemove(unitId, out _);
        return Task.CompletedTask;
    }

    public Task<JsonElement?> GetMetadataAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_slots.TryGetValue(unitId, out var slot) ? slot.Metadata : null);
    }

    public Task SetMetadataAsync(
        Guid unitId,
        JsonElement metadata,
        CancellationToken cancellationToken = default)
    {
        if (!_slots.TryGetValue(unitId, out var slot))
        {
            throw new InvalidOperationException(
                $"Unit '{unitId}' has no connector binding; cannot persist runtime metadata.");
        }
        _slots[unitId] = slot with { Metadata = metadata };
        return Task.CompletedTask;
    }

    public Task ClearMetadataAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        if (_slots.TryGetValue(unitId, out var slot))
        {
            _slots[unitId] = slot with { Metadata = null };
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Test helper: pre-seeds a binding for <paramref name="unitId"/>
    /// without exercising the partial-PATCH semantics, so tests can
    /// arrange a "unit already bound" baseline.
    /// </summary>
    public void Seed(Guid unitId, Guid connectorTypeId, JsonElement config, JsonElement? metadata = null)
    {
        _slots[unitId] = new BindingSlot(connectorTypeId, config, metadata);
    }

    private record BindingSlot(Guid ConnectorType, JsonElement Config, JsonElement? Metadata);
}
