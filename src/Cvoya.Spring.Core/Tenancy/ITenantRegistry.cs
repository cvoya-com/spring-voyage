// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tenancy;

/// <summary>
/// CRUD surface for the platform's first-class tenant registry. Backs
/// <c>/api/v1/platform/tenants</c> (C1.2d / #1260) — the CLI's
/// <c>spring tenant …</c> verbs, the portal's tenant-management view,
/// and the cloud overlay's self-onboarding flow all sit on top of this.
/// </summary>
/// <remarks>
/// <para>
/// Operations on this interface are global by design — every method
/// reads or writes across the tenant boundary. Implementations open an
/// <see cref="ITenantScopeBypass"/> scope where the underlying storage
/// engine would otherwise enforce a tenant query filter, even though
/// <see cref="TenantRecord"/> itself is not an
/// <see cref="ITenantScopedEntity"/>. The OSS scope-bypass implementation
/// audits every open in the structured log; the cloud overlay's
/// permission-checked variant additionally gates the open on the
/// caller's role.
/// </para>
/// <para>
/// The HTTP layer is the single authorisation gate: every endpoint that
/// invokes this interface requires the
/// <see cref="Cvoya.Spring.Core.Security.PlatformRoles.PlatformOperator"/>
/// role. The registry itself does not re-check role claims — pushing the
/// gate down would duplicate the check and complicate the cloud
/// overlay's role-claim source.
/// </para>
/// </remarks>
public interface ITenantRegistry
{
    /// <summary>
    /// Lists every tenant in the registry, ordered by
    /// <see cref="TenantRecord.Id"/>. Soft-deleted tenants
    /// (<see cref="TenantState.Deleted"/>) are excluded by default;
    /// callers that need to enumerate them should layer additional
    /// methods on the cloud overlay's expanded surface.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<IReadOnlyList<TenantRecord>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the tenant record by id. Returns <c>null</c> when the row
    /// is missing or has been soft-deleted.
    /// </summary>
    /// <param name="id">The tenant id to look up.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<TenantRecord?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Inserts a new tenant record. Throws when a row with the same
    /// <paramref name="id"/> already exists (active or soft-deleted).
    /// </summary>
    /// <param name="id">
    /// Stable, lower-case, slug-shaped tenant id. Must match
    /// <c>^[a-z0-9][a-z0-9_-]{0,63}$</c>; the registry enforces shape
    /// independently of the API layer to keep the contract explicit.
    /// </param>
    /// <param name="displayName">
    /// Optional human-facing display name. The registry falls back to
    /// <paramref name="id"/> when the value is null or whitespace.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The newly-created record.</returns>
    Task<TenantRecord> CreateAsync(
        string id,
        string? displayName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the mutable fields on an existing tenant record. Returns
    /// <c>null</c> when no active row matches <paramref name="id"/>.
    /// </summary>
    /// <param name="id">The tenant id to update.</param>
    /// <param name="displayName">
    /// New display name. <c>null</c> leaves the existing value
    /// unchanged; an empty / whitespace string falls back to
    /// <paramref name="id"/>.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<TenantRecord?> UpdateAsync(
        string id,
        string? displayName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes the tenant record. Returns <c>false</c> when no
    /// active row matches; <c>true</c> on success. Tenant-scoped data
    /// owned by the tenant remains in place (the registry deliberately
    /// does not cascade) so the caller can restore the tenant by
    /// re-creating it with the same id, or run a separate purge job
    /// when the data is truly stale.
    /// </summary>
    /// <param name="id">The tenant id to delete.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}