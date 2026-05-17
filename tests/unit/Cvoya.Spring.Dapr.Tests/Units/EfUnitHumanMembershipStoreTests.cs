// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Units;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Tenancy;
using Cvoya.Spring.Dapr.Units;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// EF-backed tests for <see cref="EfUnitHumanMembershipStore"/> — the
/// write half of ADR-0044 § 3 added by #2409 atop the read seam that
/// shipped with #2404. Exercises the upsert / get / remove contract plus
/// the tenant-isolation invariant inherited from the DbContext query
/// filter.
/// </summary>
public class EfUnitHumanMembershipStoreTests : IDisposable
{
    private static readonly Guid TenantA = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = new("aaaaaaaa-0000-0000-0000-000000000002");

    private readonly string _dbName = Guid.NewGuid().ToString();
    private readonly ServiceProvider _providerA;
    private readonly ServiceProvider _providerB;

    public EfUnitHumanMembershipStoreTests()
    {
        _providerA = BuildProvider(TenantA);
        _providerB = BuildProvider(TenantB);
    }

    private ServiceProvider BuildProvider(Guid tenantId)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(tenantId));
        services.AddDbContext<SpringDbContext>(o => o.UseInMemoryDatabase(_dbName));
        return services.BuildServiceProvider();
    }

    private static EfUnitHumanMembershipStore CreateStore(ServiceProvider provider) =>
        new(provider.GetRequiredService<IServiceScopeFactory>());

    [Fact]
    public async Task Upsert_NewKey_InsertsRowWithFreshGuid()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);
        var unitId = Guid.NewGuid();
        var humanId = Guid.NewGuid();

        var row = await store.UpsertAsync(unitId, humanId, "owner",
            new[] { "security" }, new[] { "escalation" }, ct);

        row.MembershipId.ShouldNotBe(Guid.Empty);
        row.HumanId.ShouldBe(humanId);
        row.Role.ShouldBe("owner");
        row.Expertise.ShouldBe(new[] { "security" });
        row.Notifications.ShouldBe(new[] { "escalation" });
    }

    [Fact]
    public async Task Upsert_ExistingKey_OverwritesProjectionsAndKeepsMembershipId()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);
        var unitId = Guid.NewGuid();
        var humanId = Guid.NewGuid();

        var first = await store.UpsertAsync(unitId, humanId, "owner",
            new[] { "old" }, new[] { "old-evt" }, ct);
        var second = await store.UpsertAsync(unitId, humanId, "owner",
            new[] { "new" }, new[] { "new-evt" }, ct);

        second.MembershipId.ShouldBe(first.MembershipId);
        second.Expertise.ShouldBe(new[] { "new" });
        second.Notifications.ShouldBe(new[] { "new-evt" });

        var rows = await store.ListByUnitAsync(unitId, ct);
        rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Upsert_NormalisesWhitespaceTags()
    {
        // The store trims whitespace and drops blank entries so the write
        // path matches the contract enforced by DefaultPackageArtefactActivator
        // — operators editing the manifest by hand and operators calling the
        // CLI should land identical projections.
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);
        var unitId = Guid.NewGuid();
        var humanId = Guid.NewGuid();

        var row = await store.UpsertAsync(unitId, humanId, "owner",
            new[] { " security ", "", "  ", "release" },
            new[] { "escalation", " " }, ct);

        row.Expertise.ShouldBe(new[] { "security", "release" });
        row.Notifications.ShouldBe(new[] { "escalation" });
    }

    [Fact]
    public async Task Upsert_SameHumanDifferentRole_InsertsSecondRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);
        var unitId = Guid.NewGuid();
        var humanId = Guid.NewGuid();

        await store.UpsertAsync(unitId, humanId, "owner",
            Array.Empty<string>(), Array.Empty<string>(), ct);
        await store.UpsertAsync(unitId, humanId, "reviewer",
            Array.Empty<string>(), Array.Empty<string>(), ct);

        var rows = await store.ListByUnitAsync(unitId, ct);
        rows.Count.ShouldBe(2);
        rows.ShouldContain(r => r.Role == "owner");
        rows.ShouldContain(r => r.Role == "reviewer");
    }

    [Fact]
    public async Task Get_MatchingKey_ReturnsRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);
        var unitId = Guid.NewGuid();
        var humanId = Guid.NewGuid();

        await store.UpsertAsync(unitId, humanId, "reviewer",
            new[] { "security" }, Array.Empty<string>(), ct);

        var row = await store.GetAsync(unitId, humanId, "reviewer", ct);

        row.ShouldNotBeNull();
        row!.HumanId.ShouldBe(humanId);
        row.Role.ShouldBe("reviewer");
    }

    [Fact]
    public async Task Get_UnknownKey_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);
        var unitId = Guid.NewGuid();

        var row = await store.GetAsync(unitId, Guid.NewGuid(), "owner", ct);

        row.ShouldBeNull();
    }

    [Fact]
    public async Task Remove_ExistingRow_ReturnsTrueAndDeletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);
        var unitId = Guid.NewGuid();
        var humanId = Guid.NewGuid();

        await store.UpsertAsync(unitId, humanId, "owner",
            Array.Empty<string>(), Array.Empty<string>(), ct);

        var removed = await store.RemoveAsync(unitId, humanId, "owner", ct);

        removed.ShouldBeTrue();
        (await store.ListByUnitAsync(unitId, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Remove_NoMatch_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);
        var unitId = Guid.NewGuid();

        var removed = await store.RemoveAsync(unitId, Guid.NewGuid(), "owner", ct);

        removed.ShouldBeFalse();
    }

    [Fact]
    public async Task TenantIsolation_TenantBCannotSeeTenantARows()
    {
        var ct = TestContext.Current.CancellationToken;
        var storeA = CreateStore(_providerA);
        var storeB = CreateStore(_providerB);
        var unitId = Guid.NewGuid();
        var humanId = Guid.NewGuid();

        await storeA.UpsertAsync(unitId, humanId, "owner",
            Array.Empty<string>(), Array.Empty<string>(), ct);

        // Tenant B's store should never see Tenant A's row — the
        // DbContext query filter applies per-tenant scoping automatically.
        (await storeB.ListByUnitAsync(unitId, ct)).ShouldBeEmpty();
        (await storeB.GetAsync(unitId, humanId, "owner", ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Upsert_WhitespaceRole_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);

        await Should.ThrowAsync<ArgumentException>(() => store.UpsertAsync(
            Guid.NewGuid(), Guid.NewGuid(), "   ",
            Array.Empty<string>(), Array.Empty<string>(), ct));
    }

    public void Dispose()
    {
        _providerA.Dispose();
        _providerB.Dispose();
        GC.SuppressFinalize(this);
    }
}
