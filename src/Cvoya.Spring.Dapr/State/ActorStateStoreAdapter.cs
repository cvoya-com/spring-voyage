// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.State;

using Cvoya.Spring.Core.State;

using global::Dapr.Actors.Runtime;

/// <summary>
/// Adapts Dapr's <see cref="IActorStateManager"/> to the <see cref="IStateStore"/> abstraction.
/// This allows actor code to use the generic <see cref="IStateStore"/> interface while still
/// being backed by Dapr's actor state management.
/// </summary>
public class ActorStateStoreAdapter(IActorStateManager stateManager) : IStateStore
{
    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        var result = await stateManager.TryGetStateAsync<T>(key, ct);
        return result.HasValue ? result.Value : default;
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        await stateManager.SetStateAsync(key, value, ct);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await stateManager.TryRemoveStateAsync(key, ct);
    }

    /// <inheritdoc />
    public async Task<bool> ContainsAsync(string key, CancellationToken ct = default)
    {
        return await stateManager.ContainsStateAsync(key, ct);
    }
}