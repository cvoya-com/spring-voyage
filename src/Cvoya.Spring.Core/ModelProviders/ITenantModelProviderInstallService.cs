// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.ModelProviders;

/// <summary>
/// Service that manages per-tenant installs of model providers
/// (ADR-0038). A provider declared in <c>eng/runtime-catalog/runtime-catalog.yaml</c>
/// is <em>available</em> to the host; an install row makes it
/// <em>visible</em> to a given tenant's wizard, CLI, and unit-creation
/// flows.
/// </summary>
/// <remarks>
/// All methods resolve the tenant via the ambient
/// <see cref="Cvoya.Spring.Core.Tenancy.ITenantContext"/> — callers do not
/// pass a <c>tenantId</c>. Cross-tenant reads require an
/// <see cref="Cvoya.Spring.Core.Tenancy.ITenantScopeBypass"/> scope.
/// </remarks>
public interface ITenantModelProviderInstallService
{
    /// <summary>
    /// Installs the provider on the current tenant or updates the existing
    /// install row. When <paramref name="config"/> is <c>null</c> the
    /// implementation materialises a config from the provider's
    /// catalogue defaults (<c>modelProviders[].defaultModels</c>).
    /// Idempotent: re-installing an already-installed provider refreshes
    /// <c>UpdatedAt</c> but does not re-issue <c>InstalledAt</c>.
    /// </summary>
    /// <param name="providerId">The provider to install.</param>
    /// <param name="config">Explicit configuration, or <c>null</c> to use the catalogue defaults.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<InstalledModelProvider> InstallAsync(
        string providerId,
        ModelProviderInstallConfig? config,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes the install row for the current tenant. No-op when the
    /// provider is not installed.
    /// </summary>
    /// <param name="providerId">The provider to uninstall.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task UninstallAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every provider installed on the current tenant, ordered by
    /// <see cref="InstalledModelProvider.ProviderId"/>.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<IReadOnlyList<InstalledModelProvider>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the install row for the current tenant or <c>null</c> when
    /// the provider is not installed.
    /// </summary>
    /// <param name="providerId">The provider to look up.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<InstalledModelProvider?> GetAsync(string providerId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the stored configuration for an already-installed provider.
    /// Throws when the provider is not installed on the current tenant.
    /// </summary>
    /// <param name="providerId">The provider whose config is being updated.</param>
    /// <param name="config">The new configuration payload.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<InstalledModelProvider> UpdateConfigAsync(
        string providerId,
        ModelProviderInstallConfig config,
        CancellationToken cancellationToken = default);
}
