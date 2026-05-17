// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Auth;

using System.Security.Claims;

using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="OssConnectorIdentityAutoSeed"/> (#2408).
/// Asserts insert-on-first-call, idempotent-on-repeat, conflict-skip when
/// another human already owns the tuple, and silent-skip when no
/// authenticated caller is present.
/// </summary>
public class OssConnectorIdentityAutoSeedTests
{
    private static readonly Guid TenantId = new("aaaa0000-0000-0000-0000-000000000001");
    private static readonly Guid OperatorHumanId = new("cccc0000-0000-0000-0000-000000000001");
    private static readonly Guid OtherHumanId = new("cccc0000-0000-0000-0000-000000000002");

    private SpringDbContext CreateContext(string dbName) =>
        new(new DbContextOptionsBuilder<SpringDbContext>().UseInMemoryDatabase(dbName).Options,
            new StaticTenantContext(TenantId));

    private static IHttpContextAccessor StubAuthenticatedContext(string username)
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        var ctx = new DefaultHttpContext();
        var identity = new ClaimsIdentity(authenticationType: "TestScheme");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, username));
        ctx.User = new ClaimsPrincipal(identity);
        accessor.HttpContext.Returns(ctx);
        return accessor;
    }

    private static IHttpContextAccessor StubAnonymousContext()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns((HttpContext?)null);
        return accessor;
    }

    private static IHumanIdentityResolver StubResolver(string username, Guid humanId)
    {
        var resolver = Substitute.For<IHumanIdentityResolver>();
        resolver.ResolveByUsernameAsync(username, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(humanId);
        return resolver;
    }

    [Fact]
    public async Task SeedForCallerAsync_InsertsRowOnFirstCall()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbName = Guid.NewGuid().ToString();

        await using var ctx = CreateContext(dbName);
        var seed = new OssConnectorIdentityAutoSeed(
            StubResolver("alice", OperatorHumanId),
            ctx,
            NullLogger<OssConnectorIdentityAutoSeed>.Instance,
            StubAuthenticatedContext("alice"));

        await seed.SeedForCallerAsync("github", "alice-login", "Alice", ct);

        var rows = await ctx.HumanConnectorIdentities.AsNoTracking().ToListAsync(ct);
        rows.Count.ShouldBe(1);
        rows[0].HumanId.ShouldBe(OperatorHumanId);
        rows[0].ConnectorId.ShouldBe("github");
        rows[0].ConnectorUserId.ShouldBe("alice-login");
        rows[0].DisplayHandle.ShouldBe("Alice");
        rows[0].TenantId.ShouldBe(TenantId);
    }

    [Fact]
    public async Task SeedForCallerAsync_IsIdempotentOnRepeat()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbName = Guid.NewGuid().ToString();

        await using var ctx = CreateContext(dbName);
        var seed = new OssConnectorIdentityAutoSeed(
            StubResolver("alice", OperatorHumanId),
            ctx,
            NullLogger<OssConnectorIdentityAutoSeed>.Instance,
            StubAuthenticatedContext("alice"));

        await seed.SeedForCallerAsync("github", "alice-login", null, ct);
        await seed.SeedForCallerAsync("github", "alice-login", null, ct);
        await seed.SeedForCallerAsync("github", "alice-login", null, ct);

        var rows = await ctx.HumanConnectorIdentities.AsNoTracking().ToListAsync(ct);
        rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SeedForCallerAsync_AnotherHumanAlreadyOwns_NoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbName = Guid.NewGuid().ToString();

        using (var pre = CreateContext(dbName))
        {
            pre.HumanConnectorIdentities.Add(new HumanConnectorIdentityEntity
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId,
                HumanId = OtherHumanId,
                ConnectorId = "github",
                ConnectorUserId = "alice-login",
            });
            await pre.SaveChangesAsync(ct);
        }

        await using var ctx = CreateContext(dbName);
        var seed = new OssConnectorIdentityAutoSeed(
            StubResolver("alice", OperatorHumanId),
            ctx,
            NullLogger<OssConnectorIdentityAutoSeed>.Instance,
            StubAuthenticatedContext("alice"));

        await seed.SeedForCallerAsync("github", "alice-login", null, ct);

        // Original row still owned by OtherHuman; no new row written.
        var rows = await ctx.HumanConnectorIdentities.AsNoTracking().ToListAsync(ct);
        rows.Count.ShouldBe(1);
        rows[0].HumanId.ShouldBe(OtherHumanId);
    }

    [Fact]
    public async Task SeedForCallerAsync_NoAuthenticatedCaller_SkipsSilently()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbName = Guid.NewGuid().ToString();

        await using var ctx = CreateContext(dbName);
        var seed = new OssConnectorIdentityAutoSeed(
            StubResolver("alice", OperatorHumanId),
            ctx,
            NullLogger<OssConnectorIdentityAutoSeed>.Instance,
            StubAnonymousContext());

        await seed.SeedForCallerAsync("github", "alice-login", null, ct);

        (await ctx.HumanConnectorIdentities.AsNoTracking().AnyAsync(ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task SeedForCallerAsync_BlankInputs_NoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var dbName = Guid.NewGuid().ToString();

        await using var ctx = CreateContext(dbName);
        var seed = new OssConnectorIdentityAutoSeed(
            StubResolver("alice", OperatorHumanId),
            ctx,
            NullLogger<OssConnectorIdentityAutoSeed>.Instance,
            StubAuthenticatedContext("alice"));

        await seed.SeedForCallerAsync(string.Empty, "alice-login", null, ct);
        await seed.SeedForCallerAsync("github", string.Empty, null, ct);
        await seed.SeedForCallerAsync("   ", "  ", null, ct);

        (await ctx.HumanConnectorIdentities.AsNoTracking().AnyAsync(ct)).ShouldBeFalse();
    }
}
