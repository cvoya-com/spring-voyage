// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Connectors;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default EF Core-backed implementation of
/// <see cref="ITenantConnectorInstallService"/>. Persists rows to
/// <c>tenant_connector_installs</c>. Connector slugs are looked up via the
/// registered <see cref="IConnectorType"/> collection so callers cannot
/// install a connector that isn't part of the host.
/// </summary>
public sealed class TenantConnectorInstallService(
    SpringDbContext dbContext,
    ITenantContext tenantContext,
    IEnumerable<IConnectorType> connectorTypes,
    ILogger<TenantConnectorInstallService> logger) : ITenantConnectorInstallService
{
    private readonly IReadOnlyList<IConnectorType> _connectorTypes = connectorTypes.ToArray();

    /// <inheritdoc />
    public async Task<InstalledConnector> InstallAsync(
        string connectorId,
        ConnectorInstallConfig? config,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        var type = ResolveOrThrow(connectorId);
        var tenantId = tenantContext.CurrentTenantId;
        var now = DateTimeOffset.UtcNow;

        // #1671: tenant-level rows are the ones where both discriminator
        // columns are null. Package-scope and unit-scope rows live in the
        // same table but are owned by the install pipeline and never
        // surfaced through this service.
        var existing = await dbContext.TenantConnectorInstalls
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                e => e.TenantId == tenantId
                    && e.ConnectorId == type.Slug
                    && e.PackageInstallId == null
                    && e.UnitId == null,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            var resolved = config ?? ConnectorInstallConfig.Empty;
            var entity = new TenantConnectorInstallEntity
            {
                Id = Guid.NewGuid(),
                TenantId = tenantId,
                ConnectorId = type.Slug,
                ConfigJson = resolved.Config,
                InstalledAt = now,
                UpdatedAt = now,
            };
            dbContext.TenantConnectorInstalls.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Installed connector '{ConnectorId}' on tenant '{TenantId}'.",
                type.Slug, tenantId);
            return Project(entity, resolved);
        }

        if (existing.DeletedAt is not null)
        {
            var resolved = config ?? ConnectorInstallConfig.Empty;
            existing.DeletedAt = null;
            existing.InstalledAt = now;
            existing.ConfigJson = resolved.Config;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Re-installed connector '{ConnectorId}' on tenant '{TenantId}' (was previously uninstalled).",
                type.Slug, tenantId);
            return Project(existing, resolved);
        }

        // Idempotent re-install: preserve existing config unless caller supplied one.
        var effective = config ?? new ConnectorInstallConfig(existing.ConfigJson);
        if (config is not null)
        {
            existing.ConfigJson = config.Config;
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogDebug(
            "Connector '{ConnectorId}' was already installed on tenant '{TenantId}'; refreshed UpdatedAt.",
            type.Slug, tenantId);
        return Project(existing, effective);
    }

    /// <inheritdoc />
    public async Task UninstallAsync(string connectorId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        var type = ResolveOrThrow(connectorId);
        var tenantId = tenantContext.CurrentTenantId;
        var existing = await dbContext.TenantConnectorInstalls
            .FirstOrDefaultAsync(
                e => e.TenantId == tenantId
                    && e.ConnectorId == type.Slug
                    && e.PackageInstallId == null
                    && e.UnitId == null,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return;
        }

        existing.DeletedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Uninstalled connector '{ConnectorId}' from tenant '{TenantId}'.",
            type.Slug, tenantId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstalledConnector>> ListAsync(CancellationToken cancellationToken = default)
    {
        // #1671: surface only tenant-level rows.
        var rows = await dbContext.TenantConnectorInstalls
            .Where(e => e.PackageInstallId == null && e.UnitId == null)
            .OrderBy(e => e.ConnectorId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows
            .Select(r => Project(r, new ConnectorInstallConfig(r.ConfigJson)))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<InstalledConnector?> GetAsync(string connectorId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);

        var type = Resolve(connectorId);
        if (type is null)
        {
            return null;
        }

        var row = await dbContext.TenantConnectorInstalls
            .FirstOrDefaultAsync(
                e => e.ConnectorId == type.Slug
                    && e.PackageInstallId == null
                    && e.UnitId == null,
                cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? null
            : Project(row, new ConnectorInstallConfig(row.ConfigJson));
    }

    /// <inheritdoc />
    public async Task<InstalledConnector> UpdateConfigAsync(
        string connectorId,
        ConnectorInstallConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorId);
        ArgumentNullException.ThrowIfNull(config);

        var type = ResolveOrThrow(connectorId);
        var tenantId = tenantContext.CurrentTenantId;
        var row = await dbContext.TenantConnectorInstalls
            .FirstOrDefaultAsync(
                e => e.ConnectorId == type.Slug
                    && e.PackageInstallId == null
                    && e.UnitId == null,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Connector '{type.Slug}' is not installed on tenant '{tenantId}'.");

        row.ConfigJson = config.Config;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Project(row, config);
    }

    private IConnectorType? Resolve(string slugOrId)
    {
        if (Guid.TryParse(slugOrId, out var id))
        {
            var byId = _connectorTypes.FirstOrDefault(c => c.TypeId == id);
            if (byId is not null)
            {
                return byId;
            }
        }
        return _connectorTypes.FirstOrDefault(
            c => string.Equals(c.Slug, slugOrId, StringComparison.OrdinalIgnoreCase));
    }

    private IConnectorType ResolveOrThrow(string slugOrId)
        => Resolve(slugOrId)
            ?? throw new InvalidOperationException(
                $"Connector '{slugOrId}' is not registered with the host.");

    private static InstalledConnector Project(
        TenantConnectorInstallEntity row,
        ConnectorInstallConfig config)
        => new(row.ConnectorId, row.TenantId, config, row.InstalledAt, row.UpdatedAt);
}