// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using System.Security.Cryptography;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Hosted service that periodically deletes <c>ActivityEventRecord</c>
/// rows older than each tenant's retention horizon. Issue #2492.
/// </summary>
/// <remarks>
/// <para>
/// Reads cross-tenant settings through <see cref="ITenantScopeBypass"/>
/// — the bypass logs the open with reason and duration so the operator
/// audit captures every cross-tenant sweep. Deletes go through a raw
/// SQL <c>DELETE</c> (via <c>ExecuteDeleteAsync</c>) so the host
/// doesn't materialise every row before evicting it.
/// </para>
/// <para>
/// The service runs at a configurable interval; the default of 1 hour
/// is fine for a 30-day retention horizon. Operators can tune via
/// <c>ActivityRetentionPurgeOptions</c>.
/// </para>
/// </remarks>
public class ActivityRetentionPurgeService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ActivityRetentionPurgeService> logger) : BackgroundService
{
    /// <summary>How often the purge loop runs.</summary>
    public static readonly TimeSpan SweepInterval = TimeSpan.FromHours(1);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Stagger the first sweep so multiple host replicas don't all
        // hit the database at the same moment after a deploy. A small
        // randomised offset is sufficient — the sweep is idempotent.
        var initialDelay = TimeSpan.FromMinutes(RandomNumberGenerator.GetInt32(0, 5));
        try
        {
            await Task.Delay(initialDelay, timeProvider, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PurgeOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Activity retention purge sweep failed.");
            }

            try
            {
                await Task.Delay(SweepInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Public test seam: runs a single sweep on demand. Idempotent —
    /// safe to call from a unit test that wants deterministic timing.
    /// </summary>
    public async Task PurgeOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var bypass = scope.ServiceProvider.GetRequiredService<ITenantScopeBypass>();
        var dbContext = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        using var auditScope = bypass.BeginBypass("activity retention purge sweep");

        // Materialise the per-tenant horizons once per sweep; the
        // sweep walks tenants in turn rather than running a single
        // multi-tenant query so each tenant's horizon is honoured
        // independently.
        var now = timeProvider.GetUtcNow();
        var settingsRows = await dbContext.TenantActivitySettings
            .AsNoTracking()
            .ToListAsync(cancellationToken);
        var perTenantHorizon = new Dictionary<Guid, int>();
        foreach (var row in settingsRows)
        {
            perTenantHorizon[row.TenantId] = row.RetentionDays > 0
                ? row.RetentionDays
                : ITenantActivitySettings.DefaultRetentionDays;
        }

        // Every distinct tenant that has activity events but no settings
        // row inherits the OSS default horizon. IgnoreQueryFilters here
        // because the tenant filter on ActivityEventRecord would otherwise
        // restrict to the ambient ITenantContext.CurrentTenantId — the
        // bypass scope opened above is intentional for this cross-tenant sweep.
        var tenantIdsWithEvents = await dbContext.ActivityEvents
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Select(e => e.TenantId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var totalDeleted = 0;
        foreach (var tenantId in tenantIdsWithEvents)
        {
            if (!perTenantHorizon.TryGetValue(tenantId, out var horizon))
            {
                horizon = ITenantActivitySettings.DefaultRetentionDays;
            }
            var cutoff = now.AddDays(-horizon);

            // ExecuteDeleteAsync is the right tool for production Postgres
            // but the in-memory test provider doesn't support it; the
            // try/catch falls back to a load+RemoveRange pass so the
            // sweep stays testable. Both paths honour the same predicate.
            int deleted;
            try
            {
                deleted = await dbContext.ActivityEvents
                    .IgnoreQueryFilters()
                    .Where(e => e.TenantId == tenantId && e.Timestamp < cutoff)
                    .ExecuteDeleteAsync(cancellationToken);
            }
            catch (InvalidOperationException) when (dbContext.Database.ProviderName?.Contains("InMemory", StringComparison.Ordinal) == true)
            {
                var rowsToDelete = await dbContext.ActivityEvents
                    .IgnoreQueryFilters()
                    .Where(e => e.TenantId == tenantId && e.Timestamp < cutoff)
                    .ToListAsync(cancellationToken);
                dbContext.ActivityEvents.RemoveRange(rowsToDelete);
                deleted = rowsToDelete.Count;
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            if (deleted > 0)
            {
                logger.LogInformation(
                    "Activity retention purge: deleted {Deleted} events for tenant {TenantId} older than {Cutoff:o} (horizon: {Horizon} days).",
                    deleted, tenantId, cutoff, horizon);
                totalDeleted += deleted;
            }
        }

        if (totalDeleted == 0)
        {
            logger.LogDebug("Activity retention purge sweep deleted no rows.");
        }
    }
}
