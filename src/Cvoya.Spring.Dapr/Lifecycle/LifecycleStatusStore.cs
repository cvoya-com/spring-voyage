// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Lifecycle;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="ILifecycleStatusStore"/>
/// (#2981). Mirrors an artefact's lifecycle status onto the existing
/// <c>agent_live_config</c> / <c>unit_live_config</c> row's
/// <c>lifecycle_status</c> column. Opens a fresh <c>IServiceScope</c> per
/// call so the scoped <see cref="SpringDbContext"/> resolves cleanly from the
/// singleton-style actor activation — the same pattern
/// <see cref="Agents.AgentLiveConfigStore"/> and
/// <see cref="Execution.PersistentAgentRegistry"/> use. The ambient
/// <c>ITenantContext</c> stamps <c>TenantId</c> on insert and scopes the read
/// via the per-entity query filter, so the mirror is tenant-isolated exactly
/// like the live-config rows it rides on.
/// </summary>
public class LifecycleStatusStore(
    IServiceScopeFactory scopeFactory,
    ILogger<LifecycleStatusStore> logger) : ILifecycleStatusStore
{
    /// <inheritdoc />
    public async Task SetStatusAsync(
        ArtefactKind kind,
        Guid artefactId,
        LifecycleStatus status,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        switch (kind)
        {
            case ArtefactKind.Agent:
                {
                    var row = await db.AgentLiveConfigs
                        .FirstOrDefaultAsync(c => c.AgentId == artefactId, cancellationToken);
                    if (row is null)
                    {
                        row = new AgentLiveConfigEntity { AgentId = artefactId };
                        db.AgentLiveConfigs.Add(row);
                    }
                    else if (row.LifecycleStatus == status)
                    {
                        // Idempotent no-op: the activation-time sync re-asserts the
                        // current status on every cold activation, so skipping the
                        // write when nothing changed keeps that path free in steady
                        // state. Transition writes always change the value, so they
                        // never hit this branch.
                        return;
                    }
                    row.LifecycleStatus = status;
                    break;
                }
            case ArtefactKind.Unit:
                {
                    var row = await db.UnitLiveConfigs
                        .FirstOrDefaultAsync(c => c.UnitId == artefactId, cancellationToken);
                    if (row is null)
                    {
                        row = new UnitLiveConfigEntity { UnitId = artefactId };
                        db.UnitLiveConfigs.Add(row);
                    }
                    else if (row.LifecycleStatus == status)
                    {
                        return;
                    }
                    row.LifecycleStatus = status;
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(kind), kind,
                    "Lifecycle status mirror only supports Unit and Agent artefacts.");
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogDebug(
            "Lifecycle status mirror set: {Kind} {ArtefactId} -> {Status}",
            kind, artefactId, status);
    }

    /// <inheritdoc />
    public async Task<LifecycleStatus?> TryGetStatusAsync(
        ArtefactKind kind,
        Guid artefactId,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        return kind switch
        {
            ArtefactKind.Agent => await db.AgentLiveConfigs
                .AsNoTracking()
                .Where(c => c.AgentId == artefactId)
                .Select(c => (LifecycleStatus?)c.LifecycleStatus)
                .FirstOrDefaultAsync(cancellationToken),
            ArtefactKind.Unit => await db.UnitLiveConfigs
                .AsNoTracking()
                .Where(c => c.UnitId == artefactId)
                .Select(c => (LifecycleStatus?)c.LifecycleStatus)
                .FirstOrDefaultAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(kind), kind,
                "Lifecycle status mirror only supports Unit and Agent artefacts."),
        };
    }
}
