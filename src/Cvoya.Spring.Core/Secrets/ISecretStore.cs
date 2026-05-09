// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Secrets;

/// <summary>
/// Backing key/value store for secret plaintext. The store is opaque:
/// callers write a plaintext value and receive back an opaque storeKey
/// (e.g. a GUID) that they record elsewhere — typically in the
/// <see cref="ISecretRegistry"/> — to retrieve the value later.
///
/// Implementations are tenant-aware via
/// <see cref="Cvoya.Spring.Core.Tenancy.ITenantContext"/> and are expected
/// to pick the correct backing component (Dapr state store, Azure Key
/// Vault, etc.) based on the current tenant. Tenant identifiers are
/// never part of the returned storeKey.
/// </summary>
public interface ISecretStore
{
    /// <summary>
    /// Writes a plaintext value to the store and returns an opaque key
    /// the caller can use to read or delete it later.
    /// </summary>
    /// <param name="plaintext">The value to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An opaque store key (e.g. a GUID string).</returns>
    Task<string> WriteAsync(string plaintext, CancellationToken ct);

    /// <summary>
    /// Reads the plaintext value for a previously-written store key,
    /// or <c>null</c> if the key does not exist.
    /// </summary>
    /// <param name="storeKey">The opaque key returned by <see cref="WriteAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The plaintext value, or <c>null</c> if not found.</returns>
    Task<string?> ReadAsync(string storeKey, CancellationToken ct);

    /// <summary>
    /// Deletes the value for the given store key. A missing key is not
    /// an error.
    /// </summary>
    /// <param name="storeKey">The opaque key returned by <see cref="WriteAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(string storeKey, CancellationToken ct);
}
