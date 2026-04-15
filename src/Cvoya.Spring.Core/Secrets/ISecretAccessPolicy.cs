// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Secrets;

/// <summary>
/// Authorization hook evaluated by the secret CRUD endpoints before
/// every operation. The OSS default implementation authorizes every
/// request (the OSS host has no notion of roles or principals). The
/// private cloud repo replaces this registration via DI with an
/// implementation that checks tenant-admin / platform-admin roles
/// against the authenticated principal.
///
/// <para>
/// Implementations receive the intended <see cref="SecretAccessAction"/>,
/// the <see cref="SecretScope"/>, and the owner id of the target — enough
/// to decide authorization without any knowledge of individual secret
/// names. The endpoints do not pass the secret name, so policy logic can
/// be applied uniformly to LIST / CREATE / DELETE without per-name
/// coupling.
/// </para>
/// </summary>
public interface ISecretAccessPolicy
{
    /// <summary>
    /// Returns whether the current request is authorized to perform the
    /// specified action on a secret in the given scope and owner. When
    /// this returns <c>false</c>, the endpoint responds with HTTP 403
    /// and the operation is NOT performed.
    /// </summary>
    /// <param name="action">The action being attempted.</param>
    /// <param name="scope">The target secret scope.</param>
    /// <param name="ownerId">
    /// The scope-specific owner id: the unit name for
    /// <see cref="SecretScope.Unit"/>; the tenant id for
    /// <see cref="SecretScope.Tenant"/>; a platform-owned identifier for
    /// <see cref="SecretScope.Platform"/>.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<bool> IsAuthorizedAsync(
        SecretAccessAction action,
        SecretScope scope,
        string ownerId,
        CancellationToken ct);
}

/// <summary>
/// The operation an <see cref="ISecretAccessPolicy"/> is being asked to
/// authorize. New values may be appended in later waves (rotation,
/// reveal, etc.) — treat the enum as open for extension.
/// </summary>
public enum SecretAccessAction
{
    /// <summary>List metadata for secrets owned by (scope, owner).</summary>
    List = 0,

    /// <summary>Create a new secret in (scope, owner).</summary>
    Create = 1,

    /// <summary>Delete a secret in (scope, owner).</summary>
    Delete = 2,

    /// <summary>
    /// Resolve a plaintext value for a secret in (scope, owner). Checked
    /// by <see cref="ISecretResolver"/> at resolve time. Critically, when
    /// a resolve traverses the Unit → Tenant inheritance fall-through the
    /// policy MUST be consulted at BOTH levels, so a caller with a unit
    /// read grant cannot obtain a tenant-scoped plaintext without a
    /// separate tenant-level grant.
    /// </summary>
    Read = 3,

    /// <summary>
    /// Rotate an existing secret in (scope, owner) — replaces the
    /// underlying value (or external pointer) and bumps the
    /// <see cref="Cvoya.Spring.Core.Secrets.SecretResolution.Version"/>.
    /// A distinct action because callers may legitimately have
    /// <see cref="Create"/> but not <see cref="Rotate"/> grants (or vice
    /// versa): creation and rotation are different operational events
    /// and deserve independent authorization.
    /// </summary>
    Rotate = 4,

    /// <summary>
    /// Prune older versions of a secret in (scope, owner) via
    /// <see cref="ISecretRegistry.PruneAsync"/> or the
    /// <c>POST /.../secrets/{name}/prune</c> endpoint. Distinct from
    /// <see cref="Delete"/> because pruning retains the current version
    /// while <see cref="Delete"/> removes every version of a secret —
    /// a caller with a "retention-admin" role may have
    /// <see cref="Prune"/> without <see cref="Delete"/>, or vice
    /// versa. Appended to the enum (rather than inserted) so existing
    /// numeric values remain stable across upgrades.
    /// </summary>
    Prune = 5,
}