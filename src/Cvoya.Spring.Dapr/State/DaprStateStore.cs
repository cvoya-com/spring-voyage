// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.State;

using Cvoya.Spring.Core.State;
using global::Dapr.Client;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Implements <see cref="IStateStore"/> using the Dapr state management building block.
/// Suitable for non-actor service code that needs key-value state persistence.
/// </summary>
public class DaprStateStore(
    DaprClient daprClient,
    IOptions<DaprStateStoreOptions> options,
    ILogger<DaprStateStore> logger) : IStateStore
{
    private readonly string _storeName = options.Value.StoreName;

    /// <inheritdoc />
    public async Task<T?> GetAsync<T>(string key, CancellationToken ct = default)
    {
        logger.LogDebug(new EventId(2300, "StateGetStarted"),
            "Getting state for key {StateKey} from store {StoreName}", key, _storeName);

        var value = await daprClient.GetStateAsync<T>(_storeName, key, cancellationToken: ct);

        logger.LogDebug(new EventId(2301, "StateGetCompleted"),
            "Get state completed for key {StateKey}, found: {Found}", key, value is not null);

        return value;
    }

    /// <inheritdoc />
    public async Task SetAsync<T>(string key, T value, CancellationToken ct = default)
    {
        logger.LogDebug(new EventId(2310, "StateSetStarted"),
            "Setting state for key {StateKey} in store {StoreName}", key, _storeName);

        await daprClient.SaveStateAsync(_storeName, key, value, cancellationToken: ct);

        logger.LogDebug(new EventId(2311, "StateSetCompleted"),
            "Set state completed for key {StateKey}", key);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        logger.LogDebug(new EventId(2320, "StateDeleteStarted"),
            "Deleting state for key {StateKey} from store {StoreName}", key, _storeName);

        await daprClient.DeleteStateAsync(_storeName, key, cancellationToken: ct);

        logger.LogDebug(new EventId(2321, "StateDeleteCompleted"),
            "Delete state completed for key {StateKey}", key);
    }

    /// <inheritdoc />
    public async Task<bool> ContainsAsync(string key, CancellationToken ct = default)
    {
        logger.LogDebug(new EventId(2330, "StateContainsStarted"),
            "Checking state existence for key {StateKey} in store {StoreName}", key, _storeName);

        var value = await daprClient.GetStateAsync<object>(_storeName, key, cancellationToken: ct);
        var exists = value is not null;

        logger.LogDebug(new EventId(2331, "StateContainsCompleted"),
            "Contains check completed for key {StateKey}, exists: {Exists}", key, exists);

        return exists;
    }
}
