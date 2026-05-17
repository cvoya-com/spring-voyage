// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Auth;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;

using Shouldly;

using Xunit;

/// <summary>
/// Tests <see cref="EfHumanConnectorIdentityResolver"/> (#2408). Pins the
/// (a) ResolveHumanAsync hit / miss / wrong-tenant contract,
/// (b) ResolveUserIdAsync hit / miss contract, and
/// (c) tenant scoping — a row in tenant A is invisible to tenant B.
/// </summary>
public class EfHumanConnectorIdentityResolverTests : IDisposable
{
    private static readonly Guid TenantA = new("aaaa0000-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = new("bbbb0000-0000-0000-0000-000000000002");
    private static readonly Guid Human1 = new("cccc0000-0000-0000-0000-000000000003");
    private static readonly Guid Human2 = new("cccc0000-0000-0000-0000-000000000004");

    private readonly string _dbName = Guid.NewGuid().ToString();

    public void Dispose()
    {
        // In-memory database scoped per test name; nothing to release.
        GC.SuppressFinalize(this);
    }

    private SpringDbContext CreateContext(Guid tenantId)
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(_dbName)
            .Options;
        return new SpringDbContext(options, new StaticTenantContext(tenantId));
    }

    [Fact]
    public async Task ResolveHumanAsync_ReturnsRowWhenPresent()
    {
        var ct = TestContext.Current.CancellationToken;

        using (var seed = CreateContext(TenantA))
        {
            seed.HumanConnectorIdentities.Add(new HumanConnectorIdentityEntity
            {
                Id = Guid.NewGuid(),
                TenantId = TenantA,
                HumanId = Human1,
                ConnectorId = "github",
                ConnectorUserId = "alice",
                DisplayHandle = "Alice McCoder",
            });
            await seed.SaveChangesAsync(ct);
        }

        using var ctx = CreateContext(TenantA);
        var resolver = new EfHumanConnectorIdentityResolver(ctx);
        var hit = await resolver.ResolveHumanAsync("github", "alice", ct);

        hit.ShouldNotBeNull();
        hit!.HumanId.ShouldBe(Human1);
        hit.ConnectorId.ShouldBe("github");
        hit.ConnectorUserId.ShouldBe("alice");
        hit.DisplayHandle.ShouldBe("Alice McCoder");
    }

    [Fact]
    public async Task ResolveHumanAsync_ReturnsNullOnMiss()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = CreateContext(TenantA);
        var resolver = new EfHumanConnectorIdentityResolver(ctx);

        (await resolver.ResolveHumanAsync("github", "ghost", ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveHumanAsync_RespectsTenantFilter()
    {
        var ct = TestContext.Current.CancellationToken;

        using (var seed = CreateContext(TenantA))
        {
            seed.HumanConnectorIdentities.Add(new HumanConnectorIdentityEntity
            {
                Id = Guid.NewGuid(),
                TenantId = TenantA,
                HumanId = Human1,
                ConnectorId = "github",
                ConnectorUserId = "alice",
            });
            await seed.SaveChangesAsync(ct);
        }

        // Different tenant — must not see TenantA's row.
        using var otherTenant = CreateContext(TenantB);
        var resolver = new EfHumanConnectorIdentityResolver(otherTenant);
        (await resolver.ResolveHumanAsync("github", "alice", ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveUserIdAsync_ReturnsLoginWhenPresent()
    {
        var ct = TestContext.Current.CancellationToken;

        using (var seed = CreateContext(TenantA))
        {
            seed.HumanConnectorIdentities.Add(new HumanConnectorIdentityEntity
            {
                Id = Guid.NewGuid(),
                TenantId = TenantA,
                HumanId = Human1,
                ConnectorId = "github",
                ConnectorUserId = "alice",
            });
            seed.HumanConnectorIdentities.Add(new HumanConnectorIdentityEntity
            {
                Id = Guid.NewGuid(),
                TenantId = TenantA,
                HumanId = Human1,
                ConnectorId = "slack",
                ConnectorUserId = "U-alice",
            });
            await seed.SaveChangesAsync(ct);
        }

        using var ctx = CreateContext(TenantA);
        var resolver = new EfHumanConnectorIdentityResolver(ctx);

        (await resolver.ResolveUserIdAsync(Human1, "github", ct)).ShouldBe("alice");
        (await resolver.ResolveUserIdAsync(Human1, "slack", ct)).ShouldBe("U-alice");
    }

    [Fact]
    public async Task ResolveUserIdAsync_ReturnsNullOnMiss()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = CreateContext(TenantA);
        var resolver = new EfHumanConnectorIdentityResolver(ctx);

        (await resolver.ResolveUserIdAsync(Human2, "github", ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveUserIdAsync_GuidEmpty_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = CreateContext(TenantA);
        var resolver = new EfHumanConnectorIdentityResolver(ctx);

        (await resolver.ResolveUserIdAsync(Guid.Empty, "github", ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveHumanAsync_BlankInputs_ReturnNull()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = CreateContext(TenantA);
        var resolver = new EfHumanConnectorIdentityResolver(ctx);

        (await resolver.ResolveHumanAsync(string.Empty, "alice", ct)).ShouldBeNull();
        (await resolver.ResolveHumanAsync("github", string.Empty, ct)).ShouldBeNull();
    }
}
