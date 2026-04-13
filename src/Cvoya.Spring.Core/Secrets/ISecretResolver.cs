// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Secrets;

/// <summary>
/// Server-side read surface for secrets. <see cref="ResolveAsync"/> is
/// the ONLY entry point through which a plaintext value may leave the
/// system: the HTTP API never returns plaintext, and every agent or
/// connector that needs a secret at runtime goes through this interface.
///
/// Implementations compose <see cref="ISecretRegistry"/> (for the
/// structural lookup) with <see cref="ISecretStore"/> (for the value
/// fetch). The private cloud repo layers audit-log decoration and RBAC
/// checks onto this interface via DI without touching call sites.
/// </summary>
public interface ISecretResolver
{
    /// <summary>
    /// Resolves the plaintext value for the given structural reference
    /// in the current tenant, or <c>null</c> if no such reference exists
    /// (or the value is missing from the underlying store).
    ///
    /// <para>
    /// For <see cref="SecretScope.Unit"/> requests this method applies
    /// the Unit → Tenant inheritance fall-through: if the unit entry is
    /// absent, the resolver transparently falls back to the same-name
    /// tenant entry, subject to the access-policy check at BOTH levels.
    /// The fall-through can be disabled via configuration — see
    /// <c>Secrets:InheritTenantFromUnit</c>.
    /// </para>
    /// </summary>
    /// <param name="ref">The structural reference.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The plaintext value, or <c>null</c>.</returns>
    Task<string?> ResolveAsync(SecretRef @ref, CancellationToken ct);

    /// <summary>
    /// Resolves the plaintext value for the given structural reference
    /// and returns the <see cref="SecretResolution"/> describing the
    /// resolve path — direct hit, inherited from tenant, or not found.
    /// This overload is what audit-log / metrics decorators consume so
    /// they can record the path taken without observing the resolver's
    /// internals. <see cref="ResolveAsync"/> is a thin convenience over
    /// this method that discards the path information.
    /// </summary>
    /// <param name="ref">The structural reference.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A <see cref="SecretResolution"/> whose <c>Path</c> is
    /// <see cref="SecretResolvePath.Direct"/>,
    /// <see cref="SecretResolvePath.InheritedFromTenant"/>, or
    /// <see cref="SecretResolvePath.NotFound"/>.
    /// </returns>
    Task<SecretResolution> ResolveWithPathAsync(SecretRef @ref, CancellationToken ct);

    /// <summary>
    /// Lists the structural references visible to the current tenant for
    /// the given scope and owner. This is a thin wrapper over
    /// <see cref="ISecretRegistry.ListAsync"/> but is the public
    /// "metadata" surface callers should prefer so audit / policy
    /// decorators see every metadata read.
    /// </summary>
    /// <param name="scope">The scope to list.</param>
    /// <param name="ownerId">The scope-specific owner id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<SecretRef>> ListAsync(SecretScope scope, string ownerId, CancellationToken ct);
}