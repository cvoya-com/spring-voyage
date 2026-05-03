// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Secrets;

/// <summary>
/// Structural metadata layer for secrets. The registry maps
/// <see cref="SecretRef"/> (scope + owner + name) to the opaque store key
/// produced by <see cref="ISecretStore"/>. All structural information
/// (tenant, scope, owner, name) lives in the registry; the store key is
/// never decoded or parsed.
///
/// Implementations enforce tenant isolation using
/// <see cref="Cvoya.Spring.Core.Tenancy.ITenantContext"/>: every operation
/// is scoped to the current tenant.
///
/// <para>
/// <b>Multi-version coexistence (wave 7 A5).</b> Each registry entry is
/// row-per-version: the unique identifier is
/// <c>(TenantId, Scope, OwnerId, Name, Version)</c>. A
/// <see cref="RegisterAsync"/> call produces a brand-new entry at
/// version 1 (replacing any existing chain). A
/// <see cref="RotateAsync"/> call inserts a NEW row at
/// <c>max(Version)+1</c> without deleting earlier versions — callers
/// can still resolve previous versions by pinning
/// <see cref="SecretRef.Version"/> (null means latest). Old versions
/// accumulate until an operator explicitly calls
/// <see cref="PruneAsync"/>.
/// </para>
///
/// <para>
/// <b>DI decoration.</b> The interface is designed for the same
/// decorate-via-DI pattern used by <see cref="ISecretResolver"/>:
/// downstream consumers (most importantly the private cloud audit-log
/// layer) register a wrapper implementation AFTER
/// <c>AddCvoyaSpringDapr</c> and re-point the <see cref="ISecretRegistry"/>
/// registration at the wrapper; the wrapper forwards to the original
/// scoped instance for the real work. <see cref="RotateAsync"/>'s
/// <see cref="SecretRotation"/> return shape is deliberately rich so
/// the decorator can emit a complete audit event from the result alone
/// — no state on the registry, no side-channel required. The new
/// <see cref="PruneAsync"/> method follows the same discipline: the
/// decorator sees every version-lifecycle operation (Register, Rotate,
/// Prune) on this single interface. See
/// <c>docs/developer/secret-audit.md</c> for a worked example.
/// </para>
/// </summary>
public interface ISecretRegistry
{
    /// <summary>
    /// Registers a fresh structural reference pointing at a store key.
    /// If a registration chain for (tenant, scope, owner, name) already
    /// exists (any versions, current or historical), every row in the
    /// chain is removed and replaced by a single new row at version
    /// <c>1</c>. Use <see cref="RotateAsync"/> to add a new version
    /// while preserving prior versions.
    ///
    /// <para>
    /// The wipe-and-replace semantics match the pre-multi-version
    /// <c>RegisterAsync</c> contract: <c>POST /secrets</c> creates a
    /// fresh chain, <c>PUT /secrets/{name}</c> rotates the chain. An
    /// audit decorator that cares about discarded prior versions can
    /// observe them via <see cref="ListVersionsAsync"/> called inside
    /// its wrapper, BEFORE forwarding to the inner registry.
    /// </para>
    /// </summary>
    /// <param name="ref">The structural reference.</param>
    /// <param name="storeKey">The opaque store key returned by <see cref="ISecretStore"/>.</param>
    /// <param name="origin">
    /// Who owns the underlying storage slot — see
    /// <see cref="SecretOrigin"/>. A <see cref="SecretOrigin.PlatformOwned"/>
    /// value means the platform wrote the plaintext via
    /// <see cref="ISecretStore.WriteAsync"/>; a
    /// <see cref="SecretOrigin.ExternalReference"/> value means the caller
    /// supplied an externally-managed key and the platform must never
    /// mutate the backing store slot.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task RegisterAsync(SecretRef @ref, string storeKey, SecretOrigin origin, CancellationToken ct);

    /// <summary>
    /// Returns the <see cref="SecretPointer"/> for the latest version of
    /// the given structural reference, or <c>null</c> if no such
    /// reference exists in the current tenant.
    /// </summary>
    /// <param name="ref">The structural reference.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<SecretPointer?> LookupAsync(SecretRef @ref, CancellationToken ct);

    /// <summary>
    /// Returns the store key for the latest version of the given
    /// structural reference, or <c>null</c> if no such reference exists
    /// in the current tenant. Prefer <see cref="LookupAsync"/> — this
    /// overload is kept for resolver paths that only need the opaque
    /// pointer and never touch the store-delete path.
    /// </summary>
    /// <param name="ref">The structural reference.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<string?> LookupStoreKeyAsync(SecretRef @ref, CancellationToken ct);

    /// <summary>
    /// Returns the <see cref="SecretPointer"/> plus the effective
    /// <c>Version</c> for either the latest version of the reference
    /// (<paramref name="version"/> = <c>null</c>) or a specific pinned
    /// version (<paramref name="version"/> = an integer).
    ///
    /// <para>
    /// Pinned reads return <c>null</c> when the requested version does
    /// not exist — they NEVER silently return a different version.
    /// Inheritance fall-through for pinned versions is applied by
    /// <see cref="ISecretResolver"/>, which calls this method twice
    /// (unit, then tenant) with the same <paramref name="version"/>
    /// argument: if the unit has no such version and the tenant does,
    /// the caller sees the tenant's version; if neither does, the
    /// caller sees <see cref="SecretResolvePath.NotFound"/>.
    /// </para>
    /// </summary>
    /// <param name="ref">The structural reference.</param>
    /// <param name="version">
    /// The version to pin to, or <c>null</c> for the latest version.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<(SecretPointer Pointer, int? Version)?> LookupWithVersionAsync(SecretRef @ref, int? version, CancellationToken ct);

    /// <summary>
    /// Latest-version convenience overload for resolver paths that never
    /// pin. Equivalent to
    /// <see cref="LookupWithVersionAsync(SecretRef, int?, CancellationToken)"/>
    /// with <paramref name="version"/> = <c>null</c>.
    /// </summary>
    /// <param name="ref">The structural reference.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<(SecretPointer Pointer, int? Version)?> LookupWithVersionAsync(SecretRef @ref, CancellationToken ct);

    /// <summary>
    /// Lists the structural references registered in the current tenant
    /// for the given scope and owner. Each unique
    /// <c>(scope, owner, name)</c> triple is returned exactly once
    /// regardless of how many versions are retained; callers that need
    /// per-version metadata use <see cref="ListVersionsAsync"/>. Order
    /// is unspecified; callers that render lists should sort
    /// client-side.
    /// </summary>
    /// <param name="scope">The scope to list.</param>
    /// <param name="ownerId">The scope-specific owner id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SecretRef>> ListAsync(SecretScope scope, Guid? ownerId, CancellationToken ct);

    /// <summary>
    /// Lists the versions retained for the given structural reference,
    /// newest first. Returns an empty list when no versions exist (the
    /// triple has never been registered, or was deleted). Each entry
    /// carries the version number, origin, creation timestamp, and a
    /// flag marking the current (latest) version.
    /// </summary>
    /// <param name="ref">The structural reference.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SecretVersionInfo>> ListVersionsAsync(SecretRef @ref, CancellationToken ct);

    /// <summary>
    /// Rotates an existing registry entry by APPENDING a new version.
    /// Atomically (within the registry's unit of work):
    /// <list type="bullet">
    ///   <item><description>Inserts a new row for the triple at version <c>max(Version)+1</c>.</description></item>
    ///   <item><description>Records <paramref name="newStoreKey"/> and <paramref name="newOrigin"/> on the new row.</description></item>
    ///   <item><description>Stamps <c>CreatedAt</c> / <c>UpdatedAt</c> on the new row.</description></item>
    ///   <item><description>LEAVES the previous version(s) intact. Pinned resolves of earlier versions continue to succeed.</description></item>
    /// </list>
    ///
    /// <para>
    /// <b>Old-slot retention flip (wave 7 A5).</b> The pre-A5 policy
    /// immediately deleted the previous platform-owned store-layer slot
    /// on rotate. With multi-version coexistence that policy inverts:
    /// the previous slot is retained so pinned resolves still work.
    /// <paramref name="deletePreviousStoreKeyAsync"/> is therefore
    /// accepted for signature compatibility but NEVER invoked by the
    /// rotate path. Store-layer slots are reclaimed only when their
    /// version row is pruned via <see cref="PruneAsync"/> or the whole
    /// chain is deleted via <see cref="DeleteAsync"/>.
    /// </para>
    ///
    /// <para>
    /// Returns a <see cref="SecretRotation"/> summarising the transition.
    /// The result is the sole input an audit-log decorator wrapping
    /// <see cref="ISecretRegistry"/> needs to emit a complete rotation
    /// event (ref, from/to versions, pointer transition, whether the
    /// old slot was reclaimed — always <c>false</c> under the multi-
    /// version policy).
    /// </para>
    /// </summary>
    /// <param name="ref">The structural reference to rotate. Must already exist in the current tenant.</param>
    /// <param name="newStoreKey">The replacement store key (platform-written key for pass-through; external pointer for external-reference).</param>
    /// <param name="newOrigin">The origin of the replacement pointer. May differ from the previous origin (e.g. rotating a platform-owned entry onto an external reference, or vice versa).</param>
    /// <param name="deletePreviousStoreKeyAsync">
    /// Retained for signature compatibility with the pre-A5 contract;
    /// never invoked under the multi-version policy. Callers may pass
    /// <c>null</c>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="SecretRotation"/> describing the transition.</returns>
    /// <exception cref="System.InvalidOperationException">
    /// The reference does not exist in the current tenant. Rotation
    /// requires an existing entry — use <see cref="RegisterAsync"/> to
    /// create one.
    /// </exception>
    Task<SecretRotation> RotateAsync(
        SecretRef @ref,
        string newStoreKey,
        SecretOrigin newOrigin,
        Func<string, CancellationToken, Task>? deletePreviousStoreKeyAsync,
        CancellationToken ct);

    /// <summary>
    /// Prunes historical versions of the given structural reference so
    /// that at most <paramref name="keep"/> most-recent versions remain.
    /// The current (latest) version is ALWAYS retained — callers who
    /// want to drop it entirely go through <see cref="DeleteAsync"/>.
    ///
    /// <para>
    /// When <paramref name="keep"/> is greater than or equal to the
    /// current version count the call is a no-op and returns
    /// <c>0</c>. When a version being pruned was platform-owned and
    /// <paramref name="deletePrunedStoreKeyAsync"/> is non-null, that
    /// delegate is invoked for each reclaimed store key — symmetric
    /// with <see cref="DeleteAsync"/>. External-reference versions are
    /// never touched.
    /// </para>
    ///
    /// <para>
    /// Returns the number of version rows pruned. An audit-log
    /// decorator wrapping the registry records this count plus the
    /// list of version numbers it saw via
    /// <see cref="ListVersionsAsync"/> pre-call.
    /// </para>
    /// </summary>
    /// <param name="ref">The structural reference to prune.</param>
    /// <param name="keep">
    /// The maximum number of most-recent versions to retain. Must be
    /// at least <c>1</c>.
    /// </param>
    /// <param name="deletePrunedStoreKeyAsync">
    /// Optional delegate invoked for each PlatformOwned store key that
    /// was pruned. <c>null</c> disables cleanup — useful for tests that
    /// assert row-removal without a real <see cref="ISecretStore"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The number of version rows removed.</returns>
    /// <exception cref="System.ArgumentOutOfRangeException">
    /// Thrown when <paramref name="keep"/> is less than <c>1</c>.
    /// </exception>
    Task<int> PruneAsync(
        SecretRef @ref,
        int keep,
        Func<string, CancellationToken, Task>? deletePrunedStoreKeyAsync,
        CancellationToken ct);

    /// <summary>
    /// Removes every version of the given structural reference from the
    /// current tenant. A missing reference is not an error. The caller
    /// is responsible for separately deleting the underlying values
    /// from <see cref="ISecretStore"/> — and must first inspect the
    /// per-version origins via <see cref="ListVersionsAsync"/> or the
    /// <paramref name="onDeleted"/> callback: only
    /// <see cref="SecretOrigin.PlatformOwned"/> pointers may be deleted
    /// through <see cref="ISecretStore.DeleteAsync"/>.
    /// </summary>
    /// <param name="ref">The structural reference.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteAsync(SecretRef @ref, CancellationToken ct);
}