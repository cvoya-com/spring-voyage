// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Routing;

using System;
using System.Linq;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Coverage for the #2972 orphaned-Hat garbage collection in
/// <see cref="DirectoryService"/>'s unit-delete cascade (ADR-0062 § 11).
/// Deleting a unit must remove its <c>unit_memberships_humans</c> rows and
/// delete any Hat that no longer belongs to any unit — fixing the original
/// "hats remain after units are deleted" bug — while preserving Hats still
/// attached to another unit and nulling a dangling
/// <c>tenant_users.primary_human_id</c>.
/// </summary>
public class DirectoryServiceHatGcTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-3333-3333-3333-000000000099");
    private static readonly Guid Operator = OssTenantUserIds.Operator;

    private readonly DirectoryCache _cache = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ServiceProvider _provider;
    private readonly DirectoryService _service;

    public DirectoryServiceHatGcTests()
    {
        _loggerFactory = Substitute.For<ILoggerFactory>();
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        // Capture the in-memory database name ONCE so every scope's
        // DbContext shares the same store (seed, register, cascade, verify).
        var dbName = $"hatgc-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(TenantId));
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(dbName));
        _provider = services.BuildServiceProvider();

        _service = new DirectoryService(
            _cache,
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _loggerFactory);
    }

    public void Dispose()
    {
        _provider.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task UnregisterAsync_unit_garbageCollectsOrphanedHat_preservesSharedHat_nullsPrimary()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitA = Guid.NewGuid();
        var unitB = Guid.NewGuid();
        var orphanHat = Guid.NewGuid();   // member of unitA only → GC'd
        var sharedHat = Guid.NewGuid();   // member of unitA AND unitB → survives

        await using (var scope = _provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.TenantUsers.Add(new TenantUserEntity
            {
                Id = Operator,
                TenantId = TenantId,
                DisplayName = "Operator",
                PrimaryHumanId = orphanHat, // dangling once the Hat is GC'd
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.Humans.Add(NewHat(orphanHat, "Orphan"));
            db.Humans.Add(NewHat(sharedHat, "Shared"));
            db.UnitMembershipsHumans.Add(HumanMembership(unitA, orphanHat));
            db.UnitMembershipsHumans.Add(HumanMembership(unitA, sharedHat));
            db.UnitMembershipsHumans.Add(HumanMembership(unitB, sharedHat));
            await db.SaveChangesAsync(ct);
        }

        var unitAddress = new Address(Address.UnitScheme, unitA);
        await _service.RegisterAsync(
            new DirectoryEntry(unitAddress, unitA, "Unit A", "", null, DateTimeOffset.UtcNow), ct);

        await _service.UnregisterAsync(unitAddress, ct);

        await using var verify = _provider.CreateAsyncScope();
        var vdb = verify.ServiceProvider.GetRequiredService<SpringDbContext>();

        // Orphaned Hat is gone; the unit's human membership rows are gone.
        (await vdb.Humans.AnyAsync(h => h.Id == orphanHat, ct)).ShouldBeFalse();
        (await vdb.UnitMembershipsHumans.AnyAsync(m => m.UnitId == unitA, ct)).ShouldBeFalse();

        // The shared Hat survives — it is still a member of unit B.
        (await vdb.Humans.AnyAsync(h => h.Id == sharedHat, ct)).ShouldBeTrue();
        (await vdb.UnitMembershipsHumans.AnyAsync(m => m.UnitId == unitB && m.HumanId == sharedHat, ct))
            .ShouldBeTrue();

        // The dangling primary pin is nulled.
        var primary = await vdb.TenantUsers
            .Where(u => u.Id == Operator)
            .Select(u => u.PrimaryHumanId)
            .SingleAsync(ct);
        primary.ShouldBeNull();
    }

    private static HumanEntity NewHat(Guid id, string name) => new()
    {
        Id = id,
        TenantId = TenantId,
        TenantUserId = Operator,
        Username = $"{name}-{id:N}",
        DisplayName = name,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static UnitMembershipHumanEntity HumanMembership(Guid unitId, Guid humanId) => new()
    {
        Id = Guid.NewGuid(),
        TenantId = TenantId,
        UnitId = unitId,
        HumanId = humanId,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
