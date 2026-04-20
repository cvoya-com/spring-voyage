// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connectors;

/// <summary>
/// Projection of a <c>tenant_connector_installs</c> row. Returned by
/// <see cref="ITenantConnectorInstallService"/> read methods.
/// </summary>
/// <param name="ConnectorId">
/// The connector slug (matches <see cref="IConnectorType.Slug"/>).
/// </param>
/// <param name="TenantId">Tenant that owns the install row.</param>
/// <param name="Config">Tenant-scoped configuration for this connector.</param>
/// <param name="InstalledAt">Timestamp when the connector was first installed.</param>
/// <param name="UpdatedAt">Timestamp when the install row was last updated.</param>
public sealed record InstalledConnector(
    string ConnectorId,
    string TenantId,
    ConnectorInstallConfig Config,
    DateTimeOffset InstalledAt,
    DateTimeOffset UpdatedAt);