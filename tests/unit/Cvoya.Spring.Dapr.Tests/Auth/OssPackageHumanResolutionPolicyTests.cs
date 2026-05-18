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
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="OssPackageHumanResolutionPolicy"/> after
/// ADR-0045 §10 — the OSS default mints a fresh <c>HumanEntity</c> per
/// declaration with a derived <c>DisplayName</c> (<c>"Operator · &lt;roles[0]&gt;"</c>
/// or <c>"Operator"</c> fallback) and returns <c>Resolved</c> with the
/// minted Guid.
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
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(_dbName));
        _provider = services.BuildServiceProvider();
    }

    private OssPackageHumanResolutionPolicy CreatePolicy() =>
        new(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OssPackageHumanResolutionPolicy>.Instance);

    private static PackageHumanResolutionRequest CreateRequest(
        string[]? roles = null,
        string? displayName = null,
        string? description = null,
        Guid? callerHumanId = null) =>
        new(
            TenantId: TenantA,
            UnitId: Guid.Parse("bbbbbbbb-2222-2222-2222-000000000001"),
            UnitDisplayName: "test-unit",
            Roles: roles ?? new[] { "owner" },
            Expertise: Array.Empty<string>(),
            Notifications: Array.Empty<string>(),
            DisplayName: displayName,
            Description: description,
            InstallCallerHumanId: callerHumanId);

    [Fact]
    public async Task ResolveAsync_DefaultBranch_MintsFreshHumanWithRoleDisplayName()
    {
        var ct = TestContext.Current.CancellationToken;
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
    }

    [Fact]
    public async Task ResolveAsync_ExplicitDisplayName_WinsOverDerivedDefault()
    {
        var ct = TestContext.Current.CancellationToken;
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
    public async Task ResolveAsync_NoRoles_FallsBackToOperator()
    {
        var ct = TestContext.Current.CancellationToken;
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
    public async Task ResolveAsync_TwoCalls_MintDistinctHumans()
    {
        // ADR-0045 §7: two declarations produce two distinct HumanEntity
        // rows even when the roles match — physical-user binding is v0.2.
        var ct = TestContext.Current.CancellationToken;
        var policy = CreatePolicy();

        var first = await policy.ResolveAsync(CreateRequest(), ct);
        var second = await policy.ResolveAsync(CreateRequest(), ct);

        first.HumanIds.ShouldHaveSingleItem();
        second.HumanIds.ShouldHaveSingleItem();
        first.HumanIds[0].ShouldNotBe(second.HumanIds[0]);
    }

    [Fact]
    public async Task ResolveAsync_PersistsDescription()
    {
        var ct = TestContext.Current.CancellationToken;
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
