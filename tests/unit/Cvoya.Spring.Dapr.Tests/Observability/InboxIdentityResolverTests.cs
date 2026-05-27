// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using System;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Observability;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="InboxIdentityResolver"/> after ADR-0062 § 7.
/// Pins the reverse-FK rule: the inbox query receives only the Humans
/// bound to the calling TenantUser, not the full tenant set.
/// </summary>
public class InboxIdentityResolverTests : IDisposable
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-3333-3333-3333-000000000099");
    private static readonly Guid CallerTenantUserId = OssTenantUserIds.Operator;

    private readonly ServiceProvider _provider;

    public InboxIdentityResolverTests()
    {
        var dbName = $"inbox-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(TenantId));
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(dbName));
        _provider = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task ResolveHumanIdsAsync_ReturnsOnlyHumansBoundToCaller()
    {
        var ct = TestContext.Current.CancellationToken;
        // Two Hats bound to the caller.
        var mineA = await SeedHumanAsync(CallerTenantUserId);
        var mineB = await SeedHumanAsync(CallerTenantUserId);
        // A Hat bound to a DIFFERENT TenantUser — must NOT appear in
        // the caller's inbox set.
        var otherTu = Guid.Parse("ffffffff-3333-3333-3333-000000000099");
        await SeedHumanAsync(otherTu);

        await using var scope = _provider.CreateAsyncScope();
        var resolver = new InboxIdentityResolver(scope.ServiceProvider.GetRequiredService<SpringDbContext>());

        var result = await resolver.ResolveHumanIdsAsync(
            new Address(Address.TenantUserScheme, CallerTenantUserId),
            ct);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(2);
        result.ShouldContain(mineA);
        result.ShouldContain(mineB);
    }

    [Fact]
    public async Task ResolveHumanIdsAsync_NoBoundHumans_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var otherTu = Guid.Parse("ffffffff-3333-3333-3333-000000000099");
        await SeedHumanAsync(otherTu);

        await using var scope = _provider.CreateAsyncScope();
        var resolver = new InboxIdentityResolver(scope.ServiceProvider.GetRequiredService<SpringDbContext>());

        var result = await resolver.ResolveHumanIdsAsync(
            new Address(Address.TenantUserScheme, CallerTenantUserId),
            ct);

        result.ShouldBeEmpty();
    }

    [Fact]
    public async Task ResolveHumanIdsAsync_NonTenantUserScheme_ReturnsEmpty()
    {
        // Defence-in-depth: a caller address not in the tenant-user scheme
        // (legacy paths, custom cloud-overlay shapes) does not leak the
        // operator's Humans by accident.
        var ct = TestContext.Current.CancellationToken;
        await SeedHumanAsync(CallerTenantUserId);

        await using var scope = _provider.CreateAsyncScope();
        var resolver = new InboxIdentityResolver(scope.ServiceProvider.GetRequiredService<SpringDbContext>());

        var result = await resolver.ResolveHumanIdsAsync(
            new Address(Address.HumanScheme, CallerTenantUserId),
            ct);

        result.ShouldBeEmpty();
    }

    private async Task<Guid> SeedHumanAsync(Guid tenantUserId)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = _provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var humanId = Guid.NewGuid();
        db.Humans.Add(new HumanEntity
        {
            Id = humanId,
            TenantId = TenantId,
            TenantUserId = tenantUserId,
            Username = $"u-{humanId:N}",
            DisplayName = $"u-{humanId:N}",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return humanId;
    }
}
