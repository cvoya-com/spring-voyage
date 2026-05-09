// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Secrets;

/// <summary>
/// Indicates who owns the underlying storage slot that a
/// <see cref="SecretRef"/> points at. This is the single piece of
/// information that tells a store-layer operation (delete, rotate,
/// re-write) whether it is allowed to mutate the backing value.
///
/// <para>
/// The OSS <see cref="ISecretStore"/> (Dapr state store) treats delete
/// on both origins as idempotent and harmless. In a private-cloud Key
/// Vault-backed implementation the distinction is load-bearing: a
/// DELETE against an <see cref="ExternalReference"/> entry MUST only
/// remove the registry row — deleting the external store key would
/// destroy a customer-owned secret that the platform merely pointed at.
/// </para>
/// </summary>
public enum SecretOrigin
{
    /// <summary>
    /// The platform wrote the plaintext value via
    /// <see cref="ISecretStore.WriteAsync"/> and owns the resulting
    /// opaque store key. Store-layer mutations are safe: rotating,
    /// re-writing, or deleting the value will only affect storage the
    /// platform itself provisioned.
    /// </summary>
    PlatformOwned = 0,

    /// <summary>
    /// The caller supplied an externally-managed store key (e.g. a Key
    /// Vault secret id) at registration time. The platform only
    /// records the pointer in the registry; it does not own the
    /// underlying value and MUST NOT perform store-layer mutations
    /// on it.
    /// </summary>
    ExternalReference = 1,
}
