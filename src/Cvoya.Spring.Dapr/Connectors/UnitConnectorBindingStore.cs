// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Connectors;

using System.Diagnostics;
using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Dapr.Data;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="IUnitConnectorBindingStore"/>.
/// Creates a fresh <c>IServiceScope</c> per call so the scoped
/// <see cref="IUnitConnectorBindingRepository"/> (and its
/// <c>SpringDbContext</c>) resolves cleanly from singleton call sites.
/// Logs the wall-clock duration of every read (<see cref="GetAsync"/>,
/// <see cref="GetMetadataAsync"/>) so the v0.2 cache decision is
/// data-driven (ADR-0040 § 3).
/// </summary>
public class UnitConnectorBindingStore(
    IServiceScopeFactory scopeFactory,
    ILogger<UnitConnectorBindingStore> logger) : IUnitConnectorBindingStore
{
    /// <inheritdoc />
    public async Task<UnitConnectorBinding?> GetAsync(
        Guid unitId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        var sw = Stopwatch.StartNew();
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitConnectorBindingRepository>();
        var result = await repo.GetAsync(unitId, cancellationToken);
        sw.Stop();
        logger.LogDebug(
            "UnitConnectorBinding.Get unit={UnitId} bound={Bound} elapsedMs={ElapsedMs}",
            unitId, result is not null, sw.Elapsed.TotalMilliseconds);
        return result;
    }

    /// <inheritdoc />
    public async Task SetAsync(
        Guid unitId,
        Guid connectorTypeId,
        JsonElement config,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitConnectorBindingRepository>();
        await repo.SetAsync(unitId, connectorTypeId, config, cancellationToken);
        logger.LogInformation(
            "Unit {UnitId} bound to connector type {TypeId}",
            unitId, connectorTypeId);
    }

    /// <inheritdoc />
    public async Task ClearAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitConnectorBindingRepository>();
        await repo.ClearAsync(unitId, cancellationToken);
        logger.LogInformation("Unit {UnitId} connector binding cleared", unitId);
    }

    /// <inheritdoc />
    public async Task<JsonElement?> GetMetadataAsync(
        Guid unitId, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitConnectorBindingRepository>();
        var result = await repo.GetMetadataAsync(unitId, cancellationToken);
        sw.Stop();
        logger.LogDebug(
            "UnitConnectorBinding.GetMetadata unit={UnitId} hasMetadata={HasMetadata} elapsedMs={ElapsedMs}",
            unitId, result is not null, sw.Elapsed.TotalMilliseconds);
        return result;
    }

    /// <inheritdoc />
    public async Task SetMetadataAsync(
        Guid unitId,
        JsonElement metadata,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitConnectorBindingRepository>();
        await repo.SetMetadataAsync(unitId, metadata, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearMetadataAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitConnectorBindingRepository>();
        await repo.ClearMetadataAsync(unitId, cancellationToken);
    }
}
