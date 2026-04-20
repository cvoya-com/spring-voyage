// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connectors;

/// <summary>
/// Service that manages per-tenant installs of
/// <see cref="IConnectorType"/> plugins. A connector registered in DI is
/// <em>available</em> to the host; an install row makes it <em>visible</em>
/// to a given tenant's wizard, CLI, and unit-binding flows.
/// </summary>
/// <remarks>
/// Parallel surface to the agent-runtime install service — same idempotent
/// install/uninstall shape, same tenant-scoping rules, different plugin
/// concept. Install rows sit one level ABOVE per-unit
/// <c>ConnectorDefinitionEntity</c> bindings: a unit can only be bound to
/// a connector type that is currently installed on its tenant.
/// </remarks>
public interface ITenantConnectorInstallService
{
    /// <summary>
    /// Installs the connector on the current tenant or updates the
    /// existing install row. Idempotent.
    /// </summary>
    /// <param name="connectorId">The connector slug (matches <see cref="IConnectorType.Slug"/>).</param>
    /// <param name="config">Explicit configuration, or <c>null</c> for an empty payload.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<InstalledConnector> InstallAsync(
        string connectorId,
        ConnectorInstallConfig? config,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Soft-deletes the install row for the current tenant. No-op when the
    /// connector is not installed.
    /// </summary>
    /// <param name="connectorId">The connector slug to uninstall.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task UninstallAsync(string connectorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists every connector installed on the current tenant, ordered by
    /// <see cref="InstalledConnector.ConnectorId"/>.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<IReadOnlyList<InstalledConnector>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Fetches the install row for the current tenant or <c>null</c> when
    /// the connector is not installed.
    /// </summary>
    /// <param name="connectorId">The connector slug to look up.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<InstalledConnector?> GetAsync(string connectorId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the stored configuration for an already-installed
    /// connector. Throws when the connector is not installed on the
    /// current tenant.
    /// </summary>
    /// <param name="connectorId">The connector whose config is being updated.</param>
    /// <param name="config">The new configuration payload.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<InstalledConnector> UpdateConfigAsync(
        string connectorId,
        ConnectorInstallConfig config,
        CancellationToken cancellationToken = default);
}