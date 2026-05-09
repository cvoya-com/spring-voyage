// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Secrets;

/// <summary>
/// The result of looking up a <see cref="SecretRef"/> in the
/// <see cref="ISecretRegistry"/>: the opaque <see cref="StoreKey"/> that
/// the <see cref="ISecretStore"/> uses to fetch the plaintext, and the
/// <see cref="Origin"/> flag that tells store-layer operations whether
/// the platform owns the underlying slot or merely points at an
/// externally-managed one.
/// </summary>
/// <param name="StoreKey">
/// Opaque pointer into the backing <see cref="ISecretStore"/>. Never
/// parsed or decoded by callers — its only consumer is the store.
/// </param>
/// <param name="Origin">
/// Who owns the backing slot. See <see cref="SecretOrigin"/> for the
/// full semantics — store-layer deletes / rotations must gate on this.
/// </param>
public record SecretPointer(string StoreKey, SecretOrigin Origin);
