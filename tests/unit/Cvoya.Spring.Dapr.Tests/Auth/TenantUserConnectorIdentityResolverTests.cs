// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Auth;

using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;

using Shouldly;

using Xunit;

/// <summary>
/// Tests <see cref="TenantUserConnectorIdentityResolver"/> (ADR-0047 §2).
/// Pins (a) the <c>ResolveTenantUserByUsernameAsync</c> hit / miss /
/// wrong-tenant contract — the reverse-lookup query the matcher uses to
/// resolve a GitHub login back to a tenant user; (b) the
/// <c>GetUsernameAsync</c> hit / miss contract — the outbound render
/// query (<c>@</c>-mention assembly, <c>--add-reviewer &lt;login&gt;</c>);
/// and (c) tenant scoping — a row in tenant A is invisible to tenant B.
/// </summary>
public class TenantUserConnectorIdentityResolverTests : IDisposable
{
    private static readonly Guid TenantA = new("aaaa0000-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = new("bbbb0000-0000-0000-0000-000000000002");
    private static readonly Guid TenantUser1 = new("cccc0000-0000-0000-0000-000000000003");
    private static readonly Guid TenantUser2 = new("cccc0000-0000-0000-0000-000000000004");

    private readonly string _dbName = Guid.NewGuid().ToString();

    public void Dispose()
    {
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
    public async Task ResolveTenantUserByUsernameAsync_ReturnsRowWhenPresent()
    {
        var ct = TestContext.Current.CancellationToken;

        using (var seed = CreateContext(TenantA))
        {
            seed.TenantUserConnectorIdentities.Add(new TenantUserConnectorIdentityEntity
            {
                Id = Guid.NewGuid(),
                TenantId = TenantA,
                TenantUserId = TenantUser1,
                ConnectorId = "github",
                Username = "alice",
                DisplayHandle = "Alice McCoder",
            });
            await seed.SaveChangesAsync(ct);
        }

        using var ctx = CreateContext(TenantA);
        var resolver = new TenantUserConnectorIdentityResolver(ctx);
        var hit = await resolver.ResolveTenantUserByUsernameAsync("github", "alice", ct);

        hit.ShouldNotBeNull();
        hit!.TenantUserId.ShouldBe(TenantUser1);
        hit.ConnectorId.ShouldBe("github");
        hit.Username.ShouldBe("alice");
        hit.DisplayHandle.ShouldBe("Alice McCoder");
    }

    [Fact]
    public async Task ResolveTenantUserByUsernameAsync_ReturnsNullOnMiss()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = CreateContext(TenantA);
        var resolver = new TenantUserConnectorIdentityResolver(ctx);

        (await resolver.ResolveTenantUserByUsernameAsync("github", "ghost", ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveTenantUserByUsernameAsync_RespectsTenantFilter()
    {
        // ADR-0047 §1: cross-tenant identity sharing is explicitly not a
        // thing. The same GitHub login mapped in TenantA must not surface
        // in a lookup running under TenantB.
        var ct = TestContext.Current.CancellationToken;

        using (var seed = CreateContext(TenantA))
        {
            seed.TenantUserConnectorIdentities.Add(new TenantUserConnectorIdentityEntity
            {
                Id = Guid.NewGuid(),
                TenantId = TenantA,
                TenantUserId = TenantUser1,
                ConnectorId = "github",
                Username = "alice",
            });
            await seed.SaveChangesAsync(ct);
        }

        using var otherTenant = CreateContext(TenantB);
        var resolver = new TenantUserConnectorIdentityResolver(otherTenant);
        (await resolver.ResolveTenantUserByUsernameAsync("github", "alice", ct)).ShouldBeNull();
    }

    [Fact]
    public async Task GetUsernameAsync_ReturnsLoginWhenPresent()
    {
        var ct = TestContext.Current.CancellationToken;

        using (var seed = CreateContext(TenantA))
        {
            seed.TenantUserConnectorIdentities.Add(new TenantUserConnectorIdentityEntity
            {
                Id = Guid.NewGuid(),
                TenantId = TenantA,
                TenantUserId = TenantUser1,
                ConnectorId = "github",
                Username = "alice",
            });
            seed.TenantUserConnectorIdentities.Add(new TenantUserConnectorIdentityEntity
            {
                Id = Guid.NewGuid(),
                TenantId = TenantA,
                TenantUserId = TenantUser1,
                ConnectorId = "slack",
                Username = "U-alice",
            });
            await seed.SaveChangesAsync(ct);
        }

        using var ctx = CreateContext(TenantA);
        var resolver = new TenantUserConnectorIdentityResolver(ctx);

        (await resolver.GetUsernameAsync(TenantUser1, "github", ct)).ShouldBe("alice");
        (await resolver.GetUsernameAsync(TenantUser1, "slack", ct)).ShouldBe("U-alice");
    }

    [Fact]
    public async Task GetUsernameAsync_ReturnsNullOnMiss()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = CreateContext(TenantA);
        var resolver = new TenantUserConnectorIdentityResolver(ctx);

        (await resolver.GetUsernameAsync(TenantUser2, "github", ct)).ShouldBeNull();
    }

    [Fact]
    public async Task GetUsernameAsync_GuidEmpty_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = CreateContext(TenantA);
        var resolver = new TenantUserConnectorIdentityResolver(ctx);

        (await resolver.GetUsernameAsync(Guid.Empty, "github", ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ResolveTenantUserByUsernameAsync_BlankInputs_ReturnNull()
    {
        var ct = TestContext.Current.CancellationToken;
        using var ctx = CreateContext(TenantA);
        var resolver = new TenantUserConnectorIdentityResolver(ctx);

        (await resolver.ResolveTenantUserByUsernameAsync(string.Empty, "alice", ct)).ShouldBeNull();
        (await resolver.ResolveTenantUserByUsernameAsync("github", string.Empty, ct)).ShouldBeNull();
    }
}
