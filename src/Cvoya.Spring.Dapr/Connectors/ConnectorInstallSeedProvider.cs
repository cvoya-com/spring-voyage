// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Connectors;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Tenant seed provider that auto-installs every DI-registered
/// <see cref="IConnectorType"/> onto the bootstrapped tenant. Parallel to
/// <see cref="AgentRuntimes.AgentRuntimeInstallSeedProvider"/>. Registered
/// as a Singleton; opens a child scope per call to resolve the scoped
/// <see cref="ITenantConnectorInstallService"/>.
/// </summary>
public sealed class ConnectorInstallSeedProvider(
    IEnumerable<IConnectorType> connectorTypes,
    IServiceScopeFactory scopeFactory,
    ILogger<ConnectorInstallSeedProvider> logger) : ITenantSeedProvider
{
    private readonly IReadOnlyList<IConnectorType> _connectorTypes = connectorTypes.ToArray();

    /// <inheritdoc />
    public string Id => "connectors";

    /// <inheritdoc />
    public int Priority => 30;

    /// <inheritdoc />
    public async Task ApplySeedsAsync(string tenantId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);

        if (_connectorTypes.Count == 0)
        {
            logger.LogInformation(
                "Tenant '{TenantId}' connector seed: no connectors registered with the host; nothing to install.",
                tenantId);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var installService = scope.ServiceProvider
            .GetRequiredService<ITenantConnectorInstallService>();

        foreach (var connector in _connectorTypes)
        {
            cancellationToken.ThrowIfCancellationRequested();

            logger.LogInformation(
                "Tenant '{TenantId}' connector seed: seeding connector '{ConnectorId}'.",
                tenantId, connector.Slug);
            await installService.InstallAsync(connector.Slug, config: null, cancellationToken)
                .ConfigureAwait(false);
        }

        logger.LogInformation(
            "Tenant '{TenantId}' connector seed: processed {Count} connector(s).",
            tenantId, _connectorTypes.Count);
    }
}