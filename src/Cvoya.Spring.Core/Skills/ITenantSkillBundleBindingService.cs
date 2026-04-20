// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

/// <summary>
/// Manages per-tenant skill-bundle bindings. A bundle is <em>discovered</em>
/// by the file-system resolver when the host starts; it is <em>visible</em>
/// to a tenant only when an <see cref="TenantSkillBundleBinding"/> row
/// with <see cref="TenantSkillBundleBinding.Enabled"/> = <c>true</c>
/// exists for that tenant.
/// </summary>
/// <remarks>
/// All methods resolve the tenant via the ambient
/// <see cref="Tenancy.ITenantContext"/> — callers never pass a tenantId.
/// V2 exposes <see cref="BindAsync"/> only to the default-tenant
/// bootstrap; a <c>spring skill-bundle …</c> CLI to mutate bindings
/// lands in V2.1.
/// </remarks>
public interface ITenantSkillBundleBindingService
{
    /// <summary>
    /// Lists every binding for the current tenant, ordered by
    /// <see cref="TenantSkillBundleBinding.BundleId"/>.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<IReadOnlyList<TenantSkillBundleBinding>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the binding for <paramref name="bundleId"/> on the current
    /// tenant, or <c>null</c> when no binding exists.
    /// </summary>
    /// <param name="bundleId">Package directory name.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<TenantSkillBundleBinding?> GetAsync(string bundleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates or updates a binding row. Idempotent — re-binding an
    /// already-bound bundle refreshes <see cref="TenantSkillBundleBinding.Enabled"/>
    /// but does not re-issue <see cref="TenantSkillBundleBinding.BoundAt"/>.
    /// Used by the default-tenant bootstrap in V2; a CLI surface is
    /// deferred to V2.1.
    /// </summary>
    /// <param name="bundleId">Package directory name.</param>
    /// <param name="enabled">Whether the bundle is visible to the tenant.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<TenantSkillBundleBinding> BindAsync(string bundleId, bool enabled, CancellationToken cancellationToken = default);
}