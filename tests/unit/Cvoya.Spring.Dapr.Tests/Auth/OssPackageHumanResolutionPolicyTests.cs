// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Auth;

using System;
using System.Linq;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Packages;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="OssPackageHumanResolutionPolicy"/> after
/// ADR-0046 §10 — the OSS default mints a fresh <c>HumanEntity</c> per
/// declaration with a derived <c>DisplayName</c>. Post #2860 the prefix
/// is sourced from the bound TenantUser's <c>DisplayName</c> (single
/// source of truth) and falls back to the literal <c>"Operator"</c> only
/// when the TenantUser row is missing or has an empty <c>DisplayName</c>.
/// </summary>
public class OssPackageHumanResolutionPolicyTests : IDisposable
{
    private static readonly Guid TenantA = Guid.Parse("aaaaaaaa-1111-1111-1111-000000000001");

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly ServiceProvider _provider;

    public OssPackageHumanResolutionPolicyTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(TenantA));
        services.AddSingleton<ITenantUserDefaultResolver, OssTenantUserDefaultResolver>();
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(_dbName));
        _provider = services.BuildServiceProvider();
    }

    private OssPackageHumanResolutionPolicy CreatePolicy() =>
        new(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OssPackageHumanResolutionPolicy>.Instance);

    /// <summary>
    /// Seed a TenantUser row for the supplied id + display name. The
    /// policy queries this row to derive the Hat prefix (#2860). In
    /// production the <c>DefaultTenantUserSeedProvider</c> materialises
    /// the operator row before any package install; tests must do the
    /// same to mirror that invariant.
    /// </summary>
    private async Task SeedTenantUserAsync(Guid id, string displayName)
    {
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.TenantUsers.Add(new TenantUserEntity
        {
            Id = id,
            TenantId = TenantA,
            DisplayName = displayName,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static PackageHumanResolutionRequest CreateRequest(
        string[]? roles = null,
        string? displayName = null,
        string? description = null,
        Guid? callerHumanId = null,
        Guid? explicitTenantUserId = null) =>
        new(
            TenantId: TenantA,
            UnitId: Guid.Parse("bbbbbbbb-2222-2222-2222-000000000001"),
            UnitDisplayName: "test-unit",
            Roles: roles ?? new[] { "owner" },
            Expertise: Array.Empty<string>(),
            Notifications: Array.Empty<string>(),
            DisplayName: displayName,
            Description: description,
            InstallCallerHumanId: callerHumanId,
            ExplicitTenantUserId: explicitTenantUserId);

    [Fact]
    public async Task ResolveAsync_DefaultBranch_MintsFreshHumanWithRoleDisplayName()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantUserAsync(OssTenantUserIds.Operator, "Operator");
        var policy = CreatePolicy();

        var resolution = await policy.ResolveAsync(
            CreateRequest(roles: new[] { "owner" }), ct);

        resolution.Outcome.ShouldBe(PackageHumanResolutionOutcome.Resolved);
        var humanId = resolution.HumanIds.ShouldHaveSingleItem();

        // The persisted HumanEntity should carry the derived DisplayName.
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.Humans.AsNoTracking().FirstOrDefaultAsync(h => h.Id == humanId, ct);
        row.ShouldNotBeNull();
        row!.DisplayName.ShouldBe("Operator · owner");
        // ADR-0062 § 1: the resolver must stamp the deployment-default
        // TenantUser FK on every minted Human row so the column is never
        // null on the wire. OSS impl returns the operator literal.
        row.TenantUserId.ShouldBe(OssTenantUserIds.Operator);
    }

    [Fact]
    public async Task ResolveAsync_ExplicitDisplayName_WinsOverDerivedDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantUserAsync(OssTenantUserIds.Operator, "Operator");
        var policy = CreatePolicy();

        var resolution = await policy.ResolveAsync(
            CreateRequest(roles: new[] { "owner" }, displayName: "Custom Operator"), ct);

        var humanId = resolution.HumanIds.ShouldHaveSingleItem();
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.Humans.AsNoTracking().FirstOrDefaultAsync(h => h.Id == humanId, ct);
        row!.DisplayName.ShouldBe("Custom Operator");
    }

    [Fact]
    public async Task ResolveAsync_NoRoles_UsesTenantUserDisplayNameVerbatim()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantUserAsync(OssTenantUserIds.Operator, "Operator");
        var policy = CreatePolicy();

        var resolution = await policy.ResolveAsync(
            CreateRequest(roles: Array.Empty<string>()), ct);

        var humanId = resolution.HumanIds.ShouldHaveSingleItem();
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.Humans.AsNoTracking().FirstOrDefaultAsync(h => h.Id == humanId, ct);
        row!.DisplayName.ShouldBe("Operator");
    }

    [Fact]
    public async Task ResolveAsync_PrefixDerivesFromTenantUserDisplayName()
    {
        // #2860: the prefix is sourced from the bound TenantUser's
        // DisplayName, not a hardcoded literal. Renaming the TenantUser
        // (production: PATCH /tenant-users/{id}) updates the prefix on
        // every subsequent Hat mint so Conversations / Engagements /
        // From-selector chips render one coherent prefix per operator.
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantUserAsync(OssTenantUserIds.Operator, "Foo");
        var policy = CreatePolicy();

        var resolution = await policy.ResolveAsync(
            CreateRequest(roles: new[] { "approver" }), ct);

        var humanId = resolution.HumanIds.ShouldHaveSingleItem();
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.Humans.AsNoTracking().FirstOrDefaultAsync(h => h.Id == humanId, ct);
        row!.DisplayName.ShouldBe("Foo · approver");
    }

    [Fact]
    public async Task ResolveAsync_ExplicitTenantUserId_PrefixDerivesFromThatRow()
    {
        // When an explicit TenantUser override is supplied, the prefix
        // should reflect *that* row's DisplayName (not the OSS-default
        // operator's). Mirrors the cloud overlay's per-principal binding.
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantUserAsync(OssTenantUserIds.Operator, "Operator");
        var explicitId = Guid.Parse("dddddddd-4444-4444-4444-000000000002");
        await SeedTenantUserAsync(explicitId, "Bar");
        var policy = CreatePolicy();

        var resolution = await policy.ResolveAsync(
            CreateRequest(roles: new[] { "approver" }, explicitTenantUserId: explicitId), ct);

        var humanId = resolution.HumanIds.ShouldHaveSingleItem();
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.Humans.AsNoTracking().FirstOrDefaultAsync(h => h.Id == humanId, ct);
        row!.DisplayName.ShouldBe("Bar · approver");
        row.TenantUserId.ShouldBe(explicitId);
    }

    [Fact]
    public async Task ResolveAsync_TenantUserMissing_FallsBackToOperatorLiteral()
    {
        // Defensive guard: when the TenantUser row hasn't been seeded
        // (malformed state — production seeder always runs first), the
        // policy still emits a sensible prefix rather than crashing or
        // emitting empty string.
        var ct = TestContext.Current.CancellationToken;
        var policy = CreatePolicy();

        var resolution = await policy.ResolveAsync(
            CreateRequest(roles: new[] { "owner" }), ct);

        var humanId = resolution.HumanIds.ShouldHaveSingleItem();
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.Humans.AsNoTracking().FirstOrDefaultAsync(h => h.Id == humanId, ct);
        row!.DisplayName.ShouldBe("Operator · owner");
    }

    [Fact]
    public async Task ResolveAsync_TwoCalls_MintDistinctHumans()
    {
        // ADR-0046 §7: two declarations produce two distinct HumanEntity
        // rows even when the roles match — physical-user binding is v0.2.
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantUserAsync(OssTenantUserIds.Operator, "Operator");
        var policy = CreatePolicy();

        var first = await policy.ResolveAsync(CreateRequest(), ct);
        var second = await policy.ResolveAsync(CreateRequest(), ct);

        first.HumanIds.ShouldHaveSingleItem();
        second.HumanIds.ShouldHaveSingleItem();
        first.HumanIds[0].ShouldNotBe(second.HumanIds[0]);
    }

    [Fact]
    public async Task ResolveAsync_ExplicitTenantUserId_WinsOverDefaultResolver()
    {
        // ADR-0062 § 6 / #2822: when the caller supplies an explicit
        // per-declaration TenantUser override, the policy stamps that
        // id on the minted HumanEntity.TenantUserId instead of asking
        // ITenantUserDefaultResolver. The endpoint validated the id
        // exists in tenant before reaching the policy.
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantUserAsync(OssTenantUserIds.Operator, "Operator");
        var policy = CreatePolicy();
        var explicitId = Guid.Parse("dddddddd-4444-4444-4444-000000000001");
        await SeedTenantUserAsync(explicitId, "Operator");

        var resolution = await policy.ResolveAsync(
            CreateRequest(explicitTenantUserId: explicitId), ct);

        var humanId = resolution.HumanIds.ShouldHaveSingleItem();
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.Humans.AsNoTracking().FirstOrDefaultAsync(h => h.Id == humanId, ct);
        row.ShouldNotBeNull();
        row!.TenantUserId.ShouldBe(explicitId);
    }

    [Fact]
    public async Task ResolveAsync_NoExplicitOverride_FallsBackToDeploymentDefaultResolver()
    {
        // The default branch — verified separately from the OSS-default
        // operator value test above so the absence of the override is
        // explicit in the contract.
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantUserAsync(OssTenantUserIds.Operator, "Operator");
        var policy = CreatePolicy();

        var resolution = await policy.ResolveAsync(
            CreateRequest(explicitTenantUserId: null), ct);

        var humanId = resolution.HumanIds.ShouldHaveSingleItem();
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.Humans.AsNoTracking().FirstOrDefaultAsync(h => h.Id == humanId, ct);
        row!.TenantUserId.ShouldBe(OssTenantUserIds.Operator);
    }

    [Fact]
    public async Task ResolveAsync_PersistsDescription()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedTenantUserAsync(OssTenantUserIds.Operator, "Operator");
        var policy = CreatePolicy();

        var resolution = await policy.ResolveAsync(
            CreateRequest(description: "Lead reviewer for the team"), ct);

        var humanId = resolution.HumanIds.ShouldHaveSingleItem();
        using var scope = _provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var row = await db.Humans.AsNoTracking().FirstOrDefaultAsync(h => h.Id == humanId, ct);
        row!.Description.ShouldBe("Lead reviewer for the team");
    }

    public void Dispose()
    {
        _provider.Dispose();
        GC.SuppressFinalize(this);
    }
}
