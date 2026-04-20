// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.AgentRuntimes;

/// <summary>
/// Service that manages per-tenant installs of
/// <see cref="IAgentRuntime"/> plugins. A runtime registered in DI is
/// <em>available</em> to the host; an install row makes it <em>visible</em>
/// to a given tenant's wizard, CLI, and unit-creation flows.
/// </summary>
/// <remarks>
/// All methods resolve the tenant via the ambient
/// <see cref="Cvoya.Spring.Core.Tenancy.ITenantContext"/> — callers do not
/// pass a <c>tenantId</c>. Cross-tenant reads require an
/// <see cref="Cvoya.Spring.Core.Tenancy.ITenantScopeBypass"/> scope.
/// </remarks>
public interface ITenantAgentRuntimeInstallService
{
    /// <summary>
    /// Installs the runtime on the current tenant or updates the existing
    /// install row. When <paramref name="config"/> is <c>null</c> the
    /// implementation materialises a config from the runtime's seed
    /// defaults (see <see cref="AgentRuntimeInstallConfig.FromRuntimeDefaults"/>).
    /// Idempotent: re-installing an already-installed runtime refreshes
    /// <c>UpdatedAt</c> but does not re-issue <c>InstalledAt</c>.
    /// </summary>
    /// <param name="runtimeId">The runtime to install (matches <see cref="IAgentRuntime.Id"/>).</param>
    /// <param name="config">Explicit configuration, or <c>null</c> to use the runtime's seed defaults.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<InstalledAgentRuntime> InstallAsync(
        string runtimeId,
        AgentRuntimeInstallConfig? config,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes the install row for the current tenant. No-op when the
    /// runtime is not installed.
    /// </summary>
    /// <param name="runtimeId">The runtime to uninstall.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task UninstallAsync(string runtimeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every runtime installed on the current tenant, ordered by
    /// <see cref="InstalledAgentRuntime.RuntimeId"/>.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<IReadOnlyList<InstalledAgentRuntime>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the install row for the current tenant or <c>null</c> when
    /// the runtime is not installed.
    /// </summary>
    /// <param name="runtimeId">The runtime to look up.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<InstalledAgentRuntime?> GetAsync(string runtimeId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the stored configuration for an already-installed runtime.
    /// Throws when the runtime is not installed on the current tenant.
    /// </summary>
    /// <param name="runtimeId">The runtime whose config is being updated.</param>
    /// <param name="config">The new configuration payload.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<InstalledAgentRuntime> UpdateConfigAsync(
        string runtimeId,
        AgentRuntimeInstallConfig config,
        CancellationToken cancellationToken = default);
}