// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tenancy;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Tenant seed provider that ensures the OSS deployment ships with exactly
/// one <c>TenantUserEntity</c> row — the operator — pinned at
/// <see cref="OssTenantUserIds.Operator"/>.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0047 § 1 names the <c>TenantUser</c> as the authenticated principal
/// of Spring Voyage scoped to one tenant. The OSS deployment ships with
/// exactly one: the operator running the install. Before this seeder
/// existed, the API host's auth path side-effect-upserted a
/// <c>HumanEntity</c> ("local-dev-user") to stand in for the missing
/// <c>TenantUser</c> — a conflation that broke both the inbox (#2766) and
/// the permission model. The seeder closes the gap by materialising the
/// canonical row at host start so the auth path can return a
/// <c>tenant-user://5c4c8e29…</c> address directly without minting any
/// extra rows.
/// </para>
/// <para>
/// Priority slot 7 — runs after the tenant record itself (priority 5) and
/// before the plugin seeders (10 / 20) so any future provider that wants
/// to declare a soft FK to <c>tenant_users.id</c> can rely on the row
/// existing. The seeder is idempotent: it inserts the row only when
/// missing and never overwrites operator edits to <c>display_name</c> /
/// <c>description</c>.
/// </para>
/// </remarks>
public sealed class DefaultTenantUserSeedProvider(
    IServiceScopeFactory scopeFactory,
    ILogger<DefaultTenantUserSeedProvider> logger) : ITenantSeedProvider
{
    /// <summary>
    /// Human-readable display name seeded for the OSS operator row.
    /// Exposed so callers (and tests) can reference the canonical literal
    /// without depending on the value text.
    /// </summary>
    public const string DefaultDisplayName = "Operator";

    /// <inheritdoc />
    public string Id => "tenant-users";

    /// <inheritdoc />
    public int Priority => 7;

    /// <inheritdoc />
    public async Task ApplySeedsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be Guid.Empty.", nameof(tenantId));
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var existing = await dbContext.TenantUsers
            .FirstOrDefaultAsync(e => e.Id == OssTenantUserIds.Operator, cancellationToken)
            .ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        if (existing is null)
        {
            dbContext.TenantUsers.Add(new TenantUserEntity
            {
                Id = OssTenantUserIds.Operator,
                TenantId = tenantId,
                AuthSubject = null,
                DisplayName = DefaultDisplayName,
                Description = null,
                CreatedAt = now,
                UpdatedAt = now,
            });
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Tenant '{TenantId}' operator user seed: inserted operator row {OperatorId}.",
                tenantId, OssTenantUserIds.Operator);
        }
        else
        {
            logger.LogInformation(
                "Tenant '{TenantId}' operator user seed: row already exists; skipped.",
                tenantId);
        }

        // ADR-0062 § 9: backfill `humans.tenant_user_id` on every existing
        // Human row to the OSS operator, and set the seeded `TenantUser`'s
        // `primary_human_id` to the first such Human if any. The
        // forward-only migration adds the FK column with a NOT NULL
        // constraint; this backfill closes the gap for rows that pre-date
        // the migration without resorting to a SQL row-migration script.
        // Idempotent: the WHERE clauses match only rows that need the
        // update.
        await BackfillHumansTenantUserIdAsync(dbContext, tenantId, cancellationToken)
            .ConfigureAwait(false);
        await BackfillPrimaryHumanIdAsync(dbContext, tenantId, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task BackfillHumansTenantUserIdAsync(
        SpringDbContext dbContext,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        // Match rows whose FK is unset (Guid.Empty) — these are pre-ADR
        // rows that landed before the FK column existed.
        var unbound = await dbContext.Humans
            .Where(h => h.TenantUserId == Guid.Empty)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (unbound.Count == 0)
        {
            return;
        }

        foreach (var row in unbound)
        {
            row.TenantUserId = OssTenantUserIds.Operator;
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Tenant '{TenantId}' operator user seed: backfilled humans.tenant_user_id on " +
            "{Count} pre-existing row(s) to operator '{OperatorId}'.",
            tenantId, unbound.Count, OssTenantUserIds.Operator);
    }

    private async Task BackfillPrimaryHumanIdAsync(
        SpringDbContext dbContext,
        Guid tenantId,
        CancellationToken cancellationToken)
    {
        var operatorRow = await dbContext.TenantUsers
            .FirstOrDefaultAsync(u => u.Id == OssTenantUserIds.Operator, cancellationToken)
            .ConfigureAwait(false);

        if (operatorRow is null || operatorRow.PrimaryHumanId is not null)
        {
            return;
        }

        var firstHumanId = await dbContext.Humans
            .AsNoTracking()
            .Where(h => h.TenantUserId == OssTenantUserIds.Operator)
            .OrderBy(h => h.CreatedAt)
            .Select(h => (Guid?)h.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (firstHumanId is null)
        {
            return;
        }

        operatorRow.PrimaryHumanId = firstHumanId;
        operatorRow.UpdatedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Tenant '{TenantId}' operator user seed: set primary_human_id={HumanId} on " +
            "operator '{OperatorId}'.",
            tenantId, firstHumanId, OssTenantUserIds.Operator);
    }
}
