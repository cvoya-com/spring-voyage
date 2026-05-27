// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Tenancy;

using System;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Regression coverage for <see cref="DefaultTenantUserSeedProvider"/>'s
/// ADR-0062 § 9 backfill behaviour: pre-existing Human rows acquire the
/// operator TenantUser id, and the operator's <c>primary_human_id</c>
/// pin lands on the first such Human if any.
/// </summary>
public class DefaultTenantUserSeedProviderTests
{
    [Fact]
    public async Task ApplySeedsAsync_FreshDb_InsertsOperatorRowWithoutPrimaryPin()
    {
        var (provider, scopeFactory) = Build();
        var tenantId = OssTenantIds.Default;

        await provider.ApplySeedsAsync(tenantId, TestContext.Current.CancellationToken);

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.TenantUsers
            .SingleAsync(u => u.Id == OssTenantUserIds.Operator, TestContext.Current.CancellationToken);

        row.PrimaryHumanId.ShouldBeNull();
        row.DisplayName.ShouldBe(DefaultTenantUserSeedProvider.DefaultDisplayName);
    }

    [Fact]
    public async Task ApplySeedsAsync_PreExistingHumans_BackfillsTenantUserId()
    {
        var (provider, scopeFactory) = Build();
        var tenantId = OssTenantIds.Default;

        // Plant a Human with TenantUserId == Guid.Empty (the pre-ADR
        // schema's default) to simulate a row that landed before the
        // migration added the FK column.
        Guid orphanedId;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            orphanedId = Guid.NewGuid();
            db.Humans.Add(new HumanEntity
            {
                Id = orphanedId,
                TenantId = tenantId,
                TenantUserId = Guid.Empty,
                Username = "pre-migration",
                DisplayName = "Pre-Migration",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await provider.ApplySeedsAsync(tenantId, TestContext.Current.CancellationToken);

        using var verifyScope = scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var human = await verifyDb.Humans
            .SingleAsync(h => h.Id == orphanedId, TestContext.Current.CancellationToken);
        human.TenantUserId.ShouldBe(OssTenantUserIds.Operator);

        // The operator's PrimaryHumanId should now pin the backfilled row.
        var operatorRow = await verifyDb.TenantUsers
            .SingleAsync(u => u.Id == OssTenantUserIds.Operator, TestContext.Current.CancellationToken);
        operatorRow.PrimaryHumanId.ShouldBe(orphanedId);
    }

    [Fact]
    public async Task ApplySeedsAsync_PreExistingHumansBoundToSomeoneElse_LeavesThemAlone()
    {
        // Cloud-overlay-style scenario: a Human is already bound to a
        // non-operator TenantUser. The OSS seeder must not "claim" it.
        var (provider, scopeFactory) = Build();
        var tenantId = OssTenantIds.Default;
        var otherTenantUser = Guid.Parse("ffffffff-1111-2222-3333-000000000001");

        Guid otherHumanId;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.TenantUsers.Add(new TenantUserEntity
            {
                Id = otherTenantUser,
                TenantId = tenantId,
                DisplayName = "Other",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            otherHumanId = Guid.NewGuid();
            db.Humans.Add(new HumanEntity
            {
                Id = otherHumanId,
                TenantId = tenantId,
                TenantUserId = otherTenantUser,
                Username = "other",
                DisplayName = "Other",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await provider.ApplySeedsAsync(tenantId, TestContext.Current.CancellationToken);

        using var verifyScope = scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var human = await verifyDb.Humans
            .SingleAsync(h => h.Id == otherHumanId, TestContext.Current.CancellationToken);
        // FK is preserved — not stomped to operator.
        human.TenantUserId.ShouldBe(otherTenantUser);

        // The operator row was seeded but has no Humans to pin, so
        // PrimaryHumanId stays null.
        var operatorRow = await verifyDb.TenantUsers
            .SingleAsync(u => u.Id == OssTenantUserIds.Operator, TestContext.Current.CancellationToken);
        operatorRow.PrimaryHumanId.ShouldBeNull();
    }

    [Fact]
    public async Task ApplySeedsAsync_Idempotent_SecondRunNoOps()
    {
        var (provider, scopeFactory) = Build();
        var tenantId = OssTenantIds.Default;

        // First run: insert operator + plant a row + backfill.
        await provider.ApplySeedsAsync(tenantId, TestContext.Current.CancellationToken);
        Guid humanId;
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            humanId = Guid.NewGuid();
            db.Humans.Add(new HumanEntity
            {
                Id = humanId,
                TenantId = tenantId,
                TenantUserId = Guid.Empty,
                Username = "round-2",
                DisplayName = "Round-2",
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Second run: backfill the new row and pin primary.
        await provider.ApplySeedsAsync(tenantId, TestContext.Current.CancellationToken);
        // Third run: must be a no-op against everything the provider owns.
        await provider.ApplySeedsAsync(tenantId, TestContext.Current.CancellationToken);

        using var verifyScope = scopeFactory.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var human = await verifyDb.Humans
            .SingleAsync(h => h.Id == humanId, TestContext.Current.CancellationToken);
        human.TenantUserId.ShouldBe(OssTenantUserIds.Operator);

        var operatorRow = await verifyDb.TenantUsers
            .SingleAsync(u => u.Id == OssTenantUserIds.Operator, TestContext.Current.CancellationToken);
        operatorRow.PrimaryHumanId.ShouldBe(humanId);
    }

    private static (DefaultTenantUserSeedProvider Provider, IServiceScopeFactory ScopeFactory) Build()
    {
        var dbName = $"tu-seed-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddDbContext<SpringDbContext>(opt => opt.UseInMemoryDatabase(dbName));
        var sp = services.BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var provider = new DefaultTenantUserSeedProvider(
            scopeFactory,
            NullLogger<DefaultTenantUserSeedProvider>.Instance);
        return (provider, scopeFactory);
    }
}
