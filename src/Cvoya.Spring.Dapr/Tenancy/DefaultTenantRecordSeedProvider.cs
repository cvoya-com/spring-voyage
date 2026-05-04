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
/// Tenant seed provider that ensures the canonical "default" tenant row
/// exists in the <c>spring.tenants</c> table introduced by C1.2d (#1260).
/// </summary>
/// <remarks>
/// <para>
/// Older OSS deployments treated the literal <c>"default"</c> as a
/// tenant id value scattered across <see cref="ITenantScopedEntity"/>
/// rows; nothing materialised the tenant as a first-class record.
/// Now that <c>/api/v1/platform/tenants</c> lists tenants from the new
/// table, the bootstrap must seed the default row so the listing is
/// non-empty on a fresh OSS host. The seeded <c>display_name</c> is the
/// human-readable literal <c>"Default Tenant"</c> — operators can rename
/// it later (via SQL today, future portal/CLI surface). The provider is
/// idempotent: it inserts the row only when missing and never overwrites
/// operator edits to <c>display_name</c>.
/// </para>
/// <para>
/// Runs at priority 5 — well before the other infrastructure seeders
/// (skill-bundles at 10, agent-runtimes at 20, …) so any future
/// provider that wants to declare a soft FK to <c>tenants.id</c> can
/// rely on the row existing.
/// </para>
/// </remarks>
public sealed class DefaultTenantRecordSeedProvider(
    IServiceScopeFactory scopeFactory,
    ILogger<DefaultTenantRecordSeedProvider> logger) : ITenantSeedProvider
{
    /// <summary>
    /// The human-readable display name seeded for the OSS bootstrap tenant.
    /// Exposed so callers (and tests) can reference the canonical literal
    /// without depending on the value text.
    /// </summary>
    public const string DefaultDisplayName = "Default Tenant";

    /// <inheritdoc />
    public string Id => "tenants";

    /// <inheritdoc />
    public int Priority => 5;

    /// <inheritdoc />
    public async Task ApplySeedsAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be Guid.Empty.", nameof(tenantId));
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // IgnoreQueryFilters so a previously-deleted row would surface;
        // the bootstrap must not silently re-insert a duplicate.
        var existing = await dbContext.Tenants
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.Id == tenantId, cancellationToken)
            .ConfigureAwait(false);

        if (existing is not null)
        {
            logger.LogInformation(
                "Tenant '{TenantId}' record seed: row already exists (deleted={Deleted}); skipped.",
                tenantId, existing.DeletedAt is not null);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        // #1661: seed a human-readable literal — the GUID-hex form previously
        // used here surfaced as a 32-char hash in the portal Explorer. Operators
        // can rename via SQL until a portal/CLI rename surface lands.
        dbContext.Tenants.Add(new TenantRecordEntity
        {
            Id = tenantId,
            DisplayName = DefaultDisplayName,
            State = TenantState.Active,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Tenant '{TenantId}' record seed: inserted default tenant row.", tenantId);
    }
}