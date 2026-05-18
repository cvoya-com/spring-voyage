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
/// write half of ADR-0044 § 3 added by #2409, reshaped by ADR-0046 §7
/// to a multi-valued <c>roles</c> jsonb column with a
/// <c>(tenant, unit, human)</c> unique key. Exercises the upsert / get /
/// remove contract plus the tenant-isolation invariant inherited from
/// the DbContext query filter.
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

        var row = await store.UpsertAsync(unitId, humanId,
            new[] { "owner" }, new[] { "security" }, new[] { "escalation" }, ct);

        row.MembershipId.ShouldNotBe(Guid.Empty);
        row.HumanId.ShouldBe(humanId);
        row.Roles.ShouldBe(new[] { "owner" });
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

        var first = await store.UpsertAsync(unitId, humanId,
            new[] { "owner" }, new[] { "old" }, new[] { "old-evt" }, ct);
        var second = await store.UpsertAsync(unitId, humanId,
            new[] { "owner", "reviewer" }, new[] { "new" }, new[] { "new-evt" }, ct);

        second.MembershipId.ShouldBe(first.MembershipId);
        second.Roles.ShouldBe(new[] { "owner", "reviewer" });
        second.Expertise.ShouldBe(new[] { "new" });
        second.Notifications.ShouldBe(new[] { "new-evt" });

        var rows = await store.ListByUnitAsync(unitId, ct);
        rows.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Upsert_NormalisesWhitespaceTags()
    {
        // The store trims whitespace and drops blank entries on every
        // multi-valued field (roles / expertise / notifications) so the
        // write path matches what the install activator persists.
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);
        var unitId = Guid.NewGuid();
        var humanId = Guid.NewGuid();

        var row = await store.UpsertAsync(unitId, humanId,
            new[] { " owner ", "", "reviewer" },
            new[] { " security ", "", "  ", "release" },
            new[] { "escalation", " " }, ct);

        row.Roles.ShouldBe(new[] { "owner", "reviewer" });
        row.Expertise.ShouldBe(new[] { "security", "release" });
        row.Notifications.ShouldBe(new[] { "escalation" });
    }

    [Fact]
    public async Task Upsert_SameHumanReapplied_CollapsesToOneRow()
    {
        // ADR-0046 §7: one row per (unit, human). Re-asserting the same
        // pair updates the row's roles list in place rather than producing
        // a second row.
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);
        var unitId = Guid.NewGuid();
        var humanId = Guid.NewGuid();

        await store.UpsertAsync(unitId, humanId,
            new[] { "owner" }, Array.Empty<string>(), Array.Empty<string>(), ct);
        await store.UpsertAsync(unitId, humanId,
            new[] { "reviewer" }, Array.Empty<string>(), Array.Empty<string>(), ct);

        var rows = await store.ListByUnitAsync(unitId, ct);
        rows.Count.ShouldBe(1);
        rows[0].Roles.ShouldBe(new[] { "reviewer" });
    }

    [Fact]
    public async Task Get_MatchingKey_ReturnsRow()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);
        var unitId = Guid.NewGuid();
        var humanId = Guid.NewGuid();

        await store.UpsertAsync(unitId, humanId,
            new[] { "reviewer" }, new[] { "security" }, Array.Empty<string>(), ct);

        var row = await store.GetAsync(unitId, humanId, ct);

        row.ShouldNotBeNull();
        row!.HumanId.ShouldBe(humanId);
        row.Roles.ShouldBe(new[] { "reviewer" });
    }

    [Fact]
    public async Task Get_UnknownKey_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);
        var unitId = Guid.NewGuid();

        var row = await store.GetAsync(unitId, Guid.NewGuid(), ct);

        row.ShouldBeNull();
    }

    [Fact]
    public async Task Remove_ExistingRow_ReturnsTrueAndDeletes()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);
        var unitId = Guid.NewGuid();
        var humanId = Guid.NewGuid();

        await store.UpsertAsync(unitId, humanId,
            new[] { "owner" }, Array.Empty<string>(), Array.Empty<string>(), ct);

        var removed = await store.RemoveAsync(unitId, humanId, ct);

        removed.ShouldBeTrue();
        (await store.ListByUnitAsync(unitId, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Remove_NoMatch_ReturnsFalse()
    {
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);
        var unitId = Guid.NewGuid();

        var removed = await store.RemoveAsync(unitId, Guid.NewGuid(), ct);

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

        await storeA.UpsertAsync(unitId, humanId,
            new[] { "owner" }, Array.Empty<string>(), Array.Empty<string>(), ct);

        // Tenant B's store should never see Tenant A's row — the
        // DbContext query filter applies per-tenant scoping automatically.
        (await storeB.ListByUnitAsync(unitId, ct)).ShouldBeEmpty();
        (await storeB.GetAsync(unitId, humanId, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task Upsert_EmptyRoles_PersistsEmptyList()
    {
        // ADR-0046 §3: empty list is a legitimate state (manifest entry
        // declared a participant without explicit roles).
        var ct = TestContext.Current.CancellationToken;
        var store = CreateStore(_providerA);

        var row = await store.UpsertAsync(
            Guid.NewGuid(), Guid.NewGuid(),
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), ct);

        row.Roles.ShouldBeEmpty();
    }

    public void Dispose()
    {
        _providerA.Dispose();
        _providerB.Dispose();
        GC.SuppressFinalize(this);
    }
}
