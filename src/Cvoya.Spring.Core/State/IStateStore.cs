// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.State;

/// <summary>
/// Abstraction for key-value state persistence.
/// Implementations may be backed by Dapr state stores, actor state managers, or in-memory stores.
/// </summary>
public interface IStateStore
{
    /// <summary>
    /// Retrieves a value by key, returning <c>default</c> if the key does not exist.
    /// </summary>
    /// <typeparam name="T">The type of the stored value.</typeparam>
    /// <param name="key">The state key.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns>The stored value, or <c>default</c> if not found.</returns>
    Task<T?> GetAsync<T>(string key, CancellationToken ct = default);

    /// <summary>
    /// Stores a value under the specified key, overwriting any existing value.
    /// </summary>
    /// <typeparam name="T">The type of the value to store.</typeparam>
    /// <param name="key">The state key.</param>
    /// <param name="value">The value to store.</param>
    /// <param name="ct">A cancellation token.</param>
    Task SetAsync<T>(string key, T value, CancellationToken ct = default);

    /// <summary>
    /// Deletes a value by key. Does nothing if the key does not exist.
    /// </summary>
    /// <param name="key">The state key.</param>
    /// <param name="ct">A cancellation token.</param>
    Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Checks whether a value exists for the specified key.
    /// </summary>
    /// <param name="key">The state key.</param>
    /// <param name="ct">A cancellation token.</param>
    /// <returns><c>true</c> if the key exists; otherwise <c>false</c>.</returns>
    Task<bool> ContainsAsync(string key, CancellationToken ct = default);
}