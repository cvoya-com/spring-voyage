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

        if (existing is not null)
        {
            logger.LogInformation(
                "Tenant '{TenantId}' operator user seed: row already exists; skipped.",
                tenantId);
            return;
        }

        var now = DateTimeOffset.UtcNow;
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
}
