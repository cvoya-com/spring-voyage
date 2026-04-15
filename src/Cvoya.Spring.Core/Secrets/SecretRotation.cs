// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Secrets;

/// <summary>
/// The outcome of an <see cref="ISecretRegistry.RotateAsync"/> call.
/// Exposes enough shape for audit-log decorators wrapping
/// <see cref="ISecretRegistry"/> to record a complete rotation event
/// without needing private registry state: <see cref="FromVersion"/> /
/// <see cref="ToVersion"/> for the version transition,
/// <see cref="PreviousPointer"/> / <see cref="NewPointer"/> for the
/// pointer transition (useful when the rotation flipped origins —
/// e.g. a platform-owned secret re-bound to an external Key Vault
/// reference), and <see cref="PreviousStoreKeyDeleted"/> so the audit
/// event can record whether the old store-layer slot was reclaimed.
/// </summary>
/// <param name="Ref">
/// The structural reference that was rotated. Identical to the
/// argument passed to <see cref="ISecretRegistry.RotateAsync"/>.
/// </param>
/// <param name="FromVersion">
/// The registry entry's version before the rotation, or <c>null</c>
/// for entries that predate the version column (legacy rows).
/// </param>
/// <param name="ToVersion">
/// The registry entry's version after the rotation. Never <c>null</c>
/// — rotation always writes a fresh version.
/// </param>
/// <param name="PreviousPointer">
/// The pointer recorded for the entry before the rotation.
/// </param>
/// <param name="NewPointer">
/// The pointer recorded after the rotation. When the rotation switched
/// origins (platform-owned ↔ external-reference), the
/// <see cref="SecretPointer.Origin"/> differs between the two
/// instances.
/// </param>
/// <param name="PreviousStoreKeyDeleted">
/// <c>true</c> when the rotation reclaimed the old backing store slot
/// (platform-owned → any origin); <c>false</c> otherwise.
///
/// <para>
/// <b>Wave 7 A5 note.</b> Under the multi-version-coexistence policy
/// this flag is always <c>false</c>: rotation APPENDS a new version
/// and leaves prior versions (and their store-layer slots) intact so
/// pinned resolves continue to work. Store-layer reclaim now happens
/// only via <see cref="ISecretRegistry.PruneAsync"/> or
/// <see cref="ISecretRegistry.DeleteAsync"/>. The field is retained on
/// this record for signature compatibility; audit decorators should
/// treat <c>false</c> as "slot retained under multi-version policy"
/// rather than "cleanup skipped due to external origin".
/// </para>
/// </param>
public record SecretRotation(
    SecretRef Ref,
    int? FromVersion,
    int ToVersion,
    SecretPointer PreviousPointer,
    SecretPointer NewPointer,
    bool PreviousStoreKeyDeleted);