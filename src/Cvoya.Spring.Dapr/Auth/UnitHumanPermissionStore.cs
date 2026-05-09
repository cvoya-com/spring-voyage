// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="IUnitHumanPermissionStore"/>.
/// Creates a fresh <c>IServiceScope</c> per call so the underlying scoped
/// <see cref="IUnitHumanPermissionRepository"/> (and its
/// <c>SpringDbContext</c>) resolves cleanly from the unit actor's
/// singleton-style activation.
/// </summary>
public class UnitHumanPermissionStore(
    IServiceScopeFactory scopeFactory,
    ILogger<UnitHumanPermissionStore> logger) : IUnitHumanPermissionStore
{
    /// <inheritdoc />
    public async Task UpsertAsync(
        Guid unitId, Guid humanId, UnitPermissionEntry entry, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitHumanPermissionRepository>();
        await repo.UpsertAsync(unitId, humanId, entry, cancellationToken);
        logger.LogInformation(
            "Unit {UnitId} ACL grant upserted for human {HumanId} at {Permission}",
            unitId, humanId, entry.Permission);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(Guid unitId, Guid humanId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitHumanPermissionRepository>();
        var deleted = await repo.DeleteAsync(unitId, humanId, cancellationToken);
        if (deleted)
        {
            logger.LogInformation(
                "Unit {UnitId} ACL grant deleted for human {HumanId}",
                unitId, humanId);
        }
        return deleted;
    }

    /// <inheritdoc />
    public async Task<PermissionLevel?> GetPermissionAsync(
        Guid unitId, Guid humanId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitHumanPermissionRepository>();
        var entry = await repo.GetAsync(unitId, humanId, cancellationToken);
        return entry?.Permission;
    }

    /// <inheritdoc />
    public async Task<UnitPermissionEntry[]> ListByUnitAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitHumanPermissionRepository>();
        var rows = await repo.ListByUnitAsync(unitId, cancellationToken);
        return rows.ToArray();
    }
}
