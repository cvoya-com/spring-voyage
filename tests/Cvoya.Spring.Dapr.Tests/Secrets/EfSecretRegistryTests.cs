// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Secrets;

using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Secrets;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="EfSecretRegistry"/> — tenant filtering is mandatory
/// per the PR spec, so the cross-tenant isolation test is parameterised
/// across the two fixture tenants.
///
/// Post #1629: tenant ids and unit/owner ids are <see cref="Guid"/>. Tests
/// declare named Guids per tenant / owner so the fixture cohort is stable.
/// </summary>
public class EfSecretRegistryTests : IDisposable
{
    // Stable fixture Guids — distinct so cross-tenant tests can pivot.
    private static readonly Guid Tenant1 = new("11111111-0000-0000-0000-000000000001");
    private static readonly Guid Tenant2 = new("22222222-0000-0000-0000-000000000002");
    private static readonly Guid TenantDefault = new("dddddddd-0000-0000-0000-000000000000");

    private static readonly Guid OwnerU1 = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OwnerMissing = new("aaaaaaaa-0000-0000-0000-00000000FFFF");

    private readonly DbContextOptions<SpringDbContext> _dbOptions;
    private readonly SpringDbContext _db;

    public EfSecretRegistryTests()
    {
        _dbOptions = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _db = new SpringDbContext(_dbOptions, new StaticTenantContext(TenantDefault));
    }

    public static IEnumerable<object[]> TenantFixtures()
    {
        yield return new object[] { Tenant1 };
        yield return new object[] { Tenant2 };
    }

    [Theory]
    [MemberData(nameof(TenantFixtures))]
    public async Task RegisterThenLookup_ReturnsStoreKey_ForSameTenant(Guid tenant)
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(tenant);

        await sut.RegisterAsync(new SecretRef(SecretScope.Unit, OwnerU1, "foo"), "sk-abc", SecretOrigin.PlatformOwned, ct);

        var found = await sut.LookupStoreKeyAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), ct);

        found.ShouldBe("sk-abc");
    }

    [Fact]
    public async Task CrossTenantIsolation_ReturnsNull_WhenEntryBelongsToDifferentTenant()
    {
        var ct = TestContext.Current.CancellationToken;

        // Tenant 2 registers (Unit, OwnerU1, "foo"). Tenant 1 must NOT
        // see it — this is the mandatory cross-tenant isolation test
        // called out in the PR spec.
        var t2 = NewRegistry(Tenant2);
        await t2.RegisterAsync(new SecretRef(SecretScope.Unit, OwnerU1, "foo"), "sk-xyz", SecretOrigin.PlatformOwned, ct);

        var t1 = NewRegistry(Tenant1);
        var found = await t1.LookupStoreKeyAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), ct);

        found.ShouldBeNull();
    }

    [Fact]
    public async Task List_ReturnsOnlyCurrentTenantEntries()
    {
        var ct = TestContext.Current.CancellationToken;

        var t1 = NewRegistry(Tenant1);
        var t2 = NewRegistry(Tenant2);

        await t1.RegisterAsync(new SecretRef(SecretScope.Unit, OwnerU1, "a"), "k-t1-a", SecretOrigin.PlatformOwned, ct);
        await t1.RegisterAsync(new SecretRef(SecretScope.Unit, OwnerU1, "b"), "k-t1-b", SecretOrigin.PlatformOwned, ct);
        await t2.RegisterAsync(new SecretRef(SecretScope.Unit, OwnerU1, "c"), "k-t2-c", SecretOrigin.PlatformOwned, ct);

        var t1List = await t1.ListAsync(SecretScope.Unit, OwnerU1, ct);
        var t2List = await t2.ListAsync(SecretScope.Unit, OwnerU1, ct);

        t1List.Select(r => r.Name).OrderBy(s => s).ShouldBe(new[] { "a", "b" });
        t2List.Select(r => r.Name).ShouldBe(new[] { "c" });
    }

    [Theory]
    [MemberData(nameof(TenantFixtures))]
    public async Task Register_SameTriple_Replaces_PreviousStoreKey(Guid tenant)
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(tenant);

        await sut.RegisterAsync(new SecretRef(SecretScope.Unit, OwnerU1, "foo"), "sk-1", SecretOrigin.PlatformOwned, ct);
        await sut.RegisterAsync(new SecretRef(SecretScope.Unit, OwnerU1, "foo"), "sk-2", SecretOrigin.PlatformOwned, ct);

        var found = await sut.LookupStoreKeyAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), ct);

        found.ShouldBe("sk-2");

        // There should be exactly one row — the earlier register was an
        // in-place update, not an insert, so the unique index on
        // (tenant, scope, owner, name) is not violated. IgnoreQueryFilters
        // to count rows across tenants from the probe DbContext.
        var count = await _db.SecretRegistryEntries.IgnoreQueryFilters().CountAsync(ct);
        count.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(TenantFixtures))]
    public async Task Delete_RemovesEntry_OnlyInCurrentTenant(Guid tenant)
    {
        var ct = TestContext.Current.CancellationToken;

        // Register identical (Unit, OwnerU1, "foo") in both tenants; only
        // the one in the deleting tenant should disappear.
        await NewRegistry(Tenant1).RegisterAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), "k-t1", SecretOrigin.PlatformOwned, ct);
        await NewRegistry(Tenant2).RegisterAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), "k-t2", SecretOrigin.PlatformOwned, ct);

        var sut = NewRegistry(tenant);
        await sut.DeleteAsync(new SecretRef(SecretScope.Unit, OwnerU1, "foo"), ct);

        var other = tenant == Tenant1 ? Tenant2 : Tenant1;
        var otherReg = NewRegistry(other);

        (await sut.LookupStoreKeyAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), ct)).ShouldBeNull();
        (await otherReg.LookupStoreKeyAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), ct)).ShouldNotBeNull();
    }

    [Fact]
    public async Task Delete_Missing_IsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        // Should not throw.
        await sut.DeleteAsync(new SecretRef(SecretScope.Unit, OwnerMissing, "none"), ct);
    }

    [Theory]
    [InlineData(SecretOrigin.PlatformOwned)]
    [InlineData(SecretOrigin.ExternalReference)]
    public async Task LookupAsync_ReturnsOrigin_AsRegistered(SecretOrigin origin)
    {
        // The origin field is load-bearing on the DELETE path — if the
        // registry loses it, the store-layer delete gate in the endpoints
        // becomes useless. Parameterise both values.
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        await sut.RegisterAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), "sk-1", origin, ct);

        var pointer = await sut.LookupAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), ct);

        pointer.ShouldNotBeNull();
        pointer!.StoreKey.ShouldBe("sk-1");
        pointer.Origin.ShouldBe(origin);
    }

    [Fact]
    public async Task LookupAsync_MissingRef_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        var pointer = await sut.LookupAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "nope"), ct);

        pointer.ShouldBeNull();
    }

    [Fact]
    public async Task Register_SameTriple_Updates_Origin_OnReplacement()
    {
        // Re-registration must replace the origin as well as the store
        // key — otherwise a platform-owned → external-reference switch
        // (or vice versa) would leave stale origin data that would
        // mis-gate the DELETE path.
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        await sut.RegisterAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), "sk-1", SecretOrigin.PlatformOwned, ct);
        await sut.RegisterAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), "kv://ext/1", SecretOrigin.ExternalReference, ct);

        var pointer = await sut.LookupAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), ct);

        pointer.ShouldNotBeNull();
        pointer!.StoreKey.ShouldBe("kv://ext/1");
        pointer.Origin.ShouldBe(SecretOrigin.ExternalReference);
    }

    [Fact]
    public async Task Register_NewEntry_StartsAtVersionOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        await sut.RegisterAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), "sk-1", SecretOrigin.PlatformOwned, ct);

        var lookup = await sut.LookupWithVersionAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), ct);

        lookup.ShouldNotBeNull();
        lookup!.Value.Version.ShouldBe(1);
    }

    [Fact]
    public async Task RotateAsync_PassThrough_IncrementsVersion_AndRetainsPreviousSlot()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        await sut.RegisterAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), "sk-1", SecretOrigin.PlatformOwned, ct);

        var deleteCalls = new List<string>();
        var rotation = await sut.RotateAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"),
            "sk-2",
            SecretOrigin.PlatformOwned,
            (key, _) => { deleteCalls.Add(key); return Task.CompletedTask; },
            ct);

        rotation.FromVersion.ShouldBe(1);
        rotation.ToVersion.ShouldBe(2);
        rotation.PreviousPointer.StoreKey.ShouldBe("sk-1");
        rotation.PreviousPointer.Origin.ShouldBe(SecretOrigin.PlatformOwned);
        rotation.NewPointer.StoreKey.ShouldBe("sk-2");
        rotation.NewPointer.Origin.ShouldBe(SecretOrigin.PlatformOwned);
        rotation.PreviousStoreKeyDeleted.ShouldBeFalse();

        // Delegate must NOT have been invoked under the retention policy.
        deleteCalls.ShouldBeEmpty();

        // The latest lookup points at the new key / bumped version.
        var pointer = await sut.LookupWithVersionAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), ct);
        pointer.ShouldNotBeNull();
        pointer!.Value.Pointer.StoreKey.ShouldBe("sk-2");
        pointer.Value.Version.ShouldBe(2);

        // The prior version is still reachable via the version pin.
        var pinned = await sut.LookupWithVersionAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), 1, ct);
        pinned.ShouldNotBeNull();
        pinned!.Value.Pointer.StoreKey.ShouldBe("sk-1");
        pinned.Value.Version.ShouldBe(1);
    }

    [Fact]
    public async Task RotateAsync_ExternalReference_SkipsDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        await sut.RegisterAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), "kv://old", SecretOrigin.ExternalReference, ct);

        var deleteCalls = new List<string>();
        var rotation = await sut.RotateAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"),
            "kv://new",
            SecretOrigin.ExternalReference,
            (key, _) => { deleteCalls.Add(key); return Task.CompletedTask; },
            ct);

        rotation.PreviousPointer.Origin.ShouldBe(SecretOrigin.ExternalReference);
        rotation.PreviousStoreKeyDeleted.ShouldBeFalse();
        deleteCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task RotateAsync_SwitchOrigin_PlatformToExternal_RetainsOldPlatformSlot()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        await sut.RegisterAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), "sk-old", SecretOrigin.PlatformOwned, ct);

        var deleteCalls = new List<string>();
        var rotation = await sut.RotateAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"),
            "kv://new",
            SecretOrigin.ExternalReference,
            (key, _) => { deleteCalls.Add(key); return Task.CompletedTask; },
            ct);

        rotation.PreviousPointer.Origin.ShouldBe(SecretOrigin.PlatformOwned);
        rotation.NewPointer.Origin.ShouldBe(SecretOrigin.ExternalReference);
        rotation.PreviousStoreKeyDeleted.ShouldBeFalse();
        deleteCalls.ShouldBeEmpty();

        var pointer = await sut.LookupAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), ct);
        pointer!.StoreKey.ShouldBe("kv://new");
        pointer.Origin.ShouldBe(SecretOrigin.ExternalReference);

        // Pinned resolve of v1 still returns the old platform-owned slot.
        var pinned = await sut.LookupWithVersionAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), 1, ct);
        pinned.ShouldNotBeNull();
        pinned!.Value.Pointer.StoreKey.ShouldBe("sk-old");
        pinned.Value.Pointer.Origin.ShouldBe(SecretOrigin.PlatformOwned);
    }

    [Fact]
    public async Task RotateAsync_MissingRef_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            sut.RotateAsync(
                new SecretRef(SecretScope.Unit, OwnerMissing, "missing"),
                "sk-new",
                SecretOrigin.PlatformOwned,
                null,
                ct));
    }

    [Fact]
    public async Task RotateAsync_NullDelegate_DoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        await sut.RegisterAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), "sk-1", SecretOrigin.PlatformOwned, ct);

        var rotation = await sut.RotateAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"),
            "sk-2",
            SecretOrigin.PlatformOwned,
            null,
            ct);

        rotation.ToVersion.ShouldBe(2);
        rotation.PreviousStoreKeyDeleted.ShouldBeFalse();
    }

    [Theory]
    [MemberData(nameof(TenantFixtures))]
    public async Task RotateAsync_CrossTenantIsolation_TreatsOtherTenantAsMissing(Guid tenant)
    {
        var ct = TestContext.Current.CancellationToken;

        var other = tenant == Tenant1 ? Tenant2 : Tenant1;
        await NewRegistry(other).RegisterAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "foo"), "sk-other", SecretOrigin.PlatformOwned, ct);

        var sut = NewRegistry(tenant);

        await Should.ThrowAsync<InvalidOperationException>(() =>
            sut.RotateAsync(
                new SecretRef(SecretScope.Unit, OwnerU1, "foo"),
                "sk-new",
                SecretOrigin.PlatformOwned,
                null,
                ct));
    }

    [Fact]
    public async Task RotateAsync_CreatesSecondRow_WithoutDeletingFirst()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        var @ref = new SecretRef(SecretScope.Unit, OwnerU1, "foo");
        await sut.RegisterAsync(@ref, "sk-1", SecretOrigin.PlatformOwned, ct);
        await sut.RotateAsync(@ref, "sk-2", SecretOrigin.PlatformOwned, null, ct);

        var versionRows = await _db.SecretRegistryEntries
            .IgnoreQueryFilters()
            .Where(e => e.Scope == SecretScope.Unit && e.OwnerId == OwnerU1 && e.Name == "foo")
            .ToListAsync(ct);
        versionRows.Count.ShouldBe(2);
        versionRows.Select(r => r.Version).OrderBy(v => v).ShouldBe(new int?[] { 1, 2 });

        var latest = await sut.LookupWithVersionAsync(@ref, ct);
        latest!.Value.Pointer.StoreKey.ShouldBe("sk-2");
        latest.Value.Version.ShouldBe(2);

        var pinned = await sut.LookupWithVersionAsync(@ref, 1, ct);
        pinned!.Value.Pointer.StoreKey.ShouldBe("sk-1");
        pinned.Value.Version.ShouldBe(1);
    }

    [Fact]
    public async Task LookupWithVersionAsync_MissingVersion_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        var @ref = new SecretRef(SecretScope.Unit, OwnerU1, "foo");
        await sut.RegisterAsync(@ref, "sk-1", SecretOrigin.PlatformOwned, ct);

        var pinned = await sut.LookupWithVersionAsync(@ref, 42, ct);
        pinned.ShouldBeNull();
    }

    [Fact]
    public async Task ListVersionsAsync_ReturnsAllVersions_NewestFirst_WithCurrentFlag()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        var @ref = new SecretRef(SecretScope.Unit, OwnerU1, "foo");
        await sut.RegisterAsync(@ref, "sk-1", SecretOrigin.PlatformOwned, ct);
        await sut.RotateAsync(@ref, "sk-2", SecretOrigin.PlatformOwned, null, ct);
        await sut.RotateAsync(@ref, "sk-3", SecretOrigin.ExternalReference, null, ct);

        var versions = await sut.ListVersionsAsync(@ref, ct);

        versions.Count.ShouldBe(3);
        versions[0].Version.ShouldBe(3);
        versions[0].IsCurrent.ShouldBeTrue();
        versions[0].Origin.ShouldBe(SecretOrigin.ExternalReference);
        versions[1].Version.ShouldBe(2);
        versions[1].IsCurrent.ShouldBeFalse();
        versions[2].Version.ShouldBe(1);
        versions[2].IsCurrent.ShouldBeFalse();
    }

    [Fact]
    public async Task ListVersionsAsync_MissingRef_ReturnsEmpty()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        var versions = await sut.ListVersionsAsync(
            new SecretRef(SecretScope.Unit, OwnerU1, "none"), ct);

        versions.ShouldBeEmpty();
    }

    [Fact]
    public async Task PruneAsync_Keep1_RemovesAllButCurrent_AndInvokesDeleteForPlatformSlots()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        var @ref = new SecretRef(SecretScope.Unit, OwnerU1, "foo");
        await sut.RegisterAsync(@ref, "sk-1", SecretOrigin.PlatformOwned, ct);
        await sut.RotateAsync(@ref, "sk-2", SecretOrigin.PlatformOwned, null, ct);
        await sut.RotateAsync(@ref, "sk-3", SecretOrigin.PlatformOwned, null, ct);

        var deletes = new List<string>();
        var pruned = await sut.PruneAsync(
            @ref,
            keep: 1,
            (key, _) => { deletes.Add(key); return Task.CompletedTask; },
            ct);

        pruned.ShouldBe(2);
        deletes.OrderBy(s => s).ShouldBe(new[] { "sk-1", "sk-2" });

        var remaining = await sut.ListVersionsAsync(@ref, ct);
        remaining.Count.ShouldBe(1);
        remaining[0].Version.ShouldBe(3);
    }

    [Fact]
    public async Task PruneAsync_KeepGteVersionCount_IsNoOp()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        var @ref = new SecretRef(SecretScope.Unit, OwnerU1, "foo");
        await sut.RegisterAsync(@ref, "sk-1", SecretOrigin.PlatformOwned, ct);
        await sut.RotateAsync(@ref, "sk-2", SecretOrigin.PlatformOwned, null, ct);

        var deletes = new List<string>();
        var pruned = await sut.PruneAsync(@ref, keep: 5,
            (key, _) => { deletes.Add(key); return Task.CompletedTask; }, ct);

        pruned.ShouldBe(0);
        deletes.ShouldBeEmpty();

        var versions = await sut.ListVersionsAsync(@ref, ct);
        versions.Count.ShouldBe(2);
    }

    [Fact]
    public async Task PruneAsync_ExternalReferenceVersions_NeverDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        var @ref = new SecretRef(SecretScope.Unit, OwnerU1, "foo");
        await sut.RegisterAsync(@ref, "kv://v1", SecretOrigin.ExternalReference, ct);
        await sut.RotateAsync(@ref, "kv://v2", SecretOrigin.ExternalReference, null, ct);

        var deletes = new List<string>();
        var pruned = await sut.PruneAsync(
            @ref, keep: 1,
            (key, _) => { deletes.Add(key); return Task.CompletedTask; },
            ct);

        pruned.ShouldBe(1);
        deletes.ShouldBeEmpty();
    }

    [Fact]
    public async Task PruneAsync_KeepZeroOrNegative_Throws()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        var @ref = new SecretRef(SecretScope.Unit, OwnerU1, "foo");
        await sut.RegisterAsync(@ref, "sk-1", SecretOrigin.PlatformOwned, ct);

        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            sut.PruneAsync(@ref, keep: 0, null, ct));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            sut.PruneAsync(@ref, keep: -1, null, ct));
    }

    [Fact]
    public async Task DeleteAsync_RemovesAllVersions()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        var @ref = new SecretRef(SecretScope.Unit, OwnerU1, "foo");
        await sut.RegisterAsync(@ref, "sk-1", SecretOrigin.PlatformOwned, ct);
        await sut.RotateAsync(@ref, "sk-2", SecretOrigin.PlatformOwned, null, ct);
        await sut.RotateAsync(@ref, "sk-3", SecretOrigin.PlatformOwned, null, ct);

        await sut.DeleteAsync(@ref, ct);

        (await _db.SecretRegistryEntries
            .IgnoreQueryFilters()
            .Where(e => e.Scope == SecretScope.Unit && e.OwnerId == OwnerU1 && e.Name == "foo")
            .CountAsync(ct)).ShouldBe(0);
    }

    [Fact]
    public async Task RegisterAsync_ExistingChain_WipesPriorVersions_AndResetsToVersionOne()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = NewRegistry(Tenant1);

        var @ref = new SecretRef(SecretScope.Unit, OwnerU1, "foo");
        await sut.RegisterAsync(@ref, "sk-1", SecretOrigin.PlatformOwned, ct);
        await sut.RotateAsync(@ref, "sk-2", SecretOrigin.PlatformOwned, null, ct);

        await sut.RegisterAsync(@ref, "sk-new", SecretOrigin.PlatformOwned, ct);

        var versions = await sut.ListVersionsAsync(@ref, ct);
        versions.Count.ShouldBe(1);
        versions[0].Version.ShouldBe(1);

        var latest = await sut.LookupAsync(@ref, ct);
        latest!.StoreKey.ShouldBe("sk-new");
    }

    private EfSecretRegistry NewRegistry(Guid tenantId)
    {
        // Each registry gets its own DbContext pinned to the tenant
        // under test, so the DbContext-level query filter matches what
        // the registry persists on writes. Both contexts share the same
        // in-memory database so cross-tenant isolation is exercised on
        // the same backing store.
        var tenantContext = new StaticTenantContext(tenantId);
        var db = new SpringDbContext(_dbOptions, tenantContext);
        return new EfSecretRegistry(db, tenantContext);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }
}
