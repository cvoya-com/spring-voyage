// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Tenancy;

using System;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Regression coverage for <see cref="DefaultTenantRecordSeedProvider"/>.
/// Pins the OSS bootstrap behaviour: the seeded tenant row must carry a
/// human-readable <c>display_name</c> literal, never the GUID-hex form
/// of the id (#1661). Idempotency (do-not-overwrite operator edits) is
/// also pinned.
/// </summary>
public class DefaultTenantRecordSeedProviderTests
{
    [Fact]
    public async Task ApplySeedsAsync_FreshDb_SeedsRowWithHumanReadableDisplayName()
    {
        var (provider, scopeFactory) = BuildProvider();
        var tenantId = OssTenantIds.Default;

        await provider.ApplySeedsAsync(tenantId, TestContext.Current.CancellationToken);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.Tenants
            .IgnoreQueryFilters()
            .SingleAsync(e => e.Id == tenantId, TestContext.Current.CancellationToken);

        row.DisplayName.ShouldBe(DefaultTenantRecordSeedProvider.DefaultDisplayName);
        row.DisplayName.ShouldBe("Default Tenant");

        // #1661 regression: must NOT be the GUID-hex of the id (the previous
        // default that surfaced as a 32-char hash in the portal Explorer).
        row.DisplayName.ShouldNotBe(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(tenantId));
    }

    [Fact]
    public async Task ApplySeedsAsync_RowAlreadyExists_DoesNotOverwriteOperatorEdit()
    {
        var (provider, scopeFactory) = BuildProvider();
        var tenantId = OssTenantIds.Default;

        // Simulate an operator who already renamed the tenant.
        const string operatorEditedName = "Acme Inc.";
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.Tenants.Add(new Cvoya.Spring.Dapr.Data.Entities.TenantRecordEntity
            {
                Id = tenantId,
                DisplayName = operatorEditedName,
                State = TenantState.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await provider.ApplySeedsAsync(tenantId, TestContext.Current.CancellationToken);

        using var verifyScope = scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await verifyDb.Tenants
            .IgnoreQueryFilters()
            .SingleAsync(e => e.Id == tenantId, TestContext.Current.CancellationToken);

        row.DisplayName.ShouldBe(operatorEditedName);
    }

    [Fact]
    public async Task ApplySeedsAsync_GuidEmpty_Throws()
    {
        var (provider, _) = BuildProvider();

        await Should.ThrowAsync<ArgumentException>(() =>
            provider.ApplySeedsAsync(Guid.Empty, TestContext.Current.CancellationToken));
    }

    private static (DefaultTenantRecordSeedProvider Provider, IServiceScopeFactory ScopeFactory) BuildProvider()
    {
        // Capture the DB name in a local so the options-builder callback —
        // which fires once per scope when DbContext options are scoped —
        // resolves to the same name every time. (Mirrors the pattern in
        // DbExpertiseSeedProviderTests.)
        var dbName = $"tenant-record-seed-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<SpringDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var provider = new DefaultTenantRecordSeedProvider(
            scopeFactory,
            NullLogger<DefaultTenantRecordSeedProvider>.Instance);
        return (provider, scopeFactory);
    }
}