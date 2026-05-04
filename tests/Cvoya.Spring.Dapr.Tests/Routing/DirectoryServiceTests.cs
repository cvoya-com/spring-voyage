// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Routing;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Routing;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="DirectoryService"/>.
/// Uses an in-memory EF Core database to validate write-through persistence.
///
/// Post #1629: identity is the entity Guid (==<c>Address.Id</c>==<c>DirectoryEntry.ActorId</c>).
/// There is no slug column on agents/units; tests use named Guid constants.
/// </summary>
public class DirectoryServiceTests : IDisposable
{
    private readonly DirectoryCache _cache = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ServiceProvider _serviceProvider;
    private readonly DirectoryService _service;
    private readonly string _dbName = $"DirectoryTests_{Guid.NewGuid()}";

    public DirectoryServiceTests()
    {
        _loggerFactory = Substitute.For<ILoggerFactory>();
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var services = new ServiceCollection();
        services.AddDbContext<SpringDbContext>(options =>
            options.UseInMemoryDatabase(_dbName));

        _serviceProvider = services.BuildServiceProvider();

        _service = new DirectoryService(
            _cache,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _loggerFactory);
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task RegisterAsync_and_ResolveAsync_returns_correct_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        var actorId = Guid.NewGuid();
        var address = new Address("agent", actorId);
        var entry = new DirectoryEntry(address, actorId, "Ada", "Backend engineer", "backend-engineer", DateTimeOffset.UtcNow);

        await _service.RegisterAsync(entry, ct);

        var resolved = await _service.ResolveAsync(address, ct);

        resolved.ShouldNotBeNull();
        resolved!.ActorId.ShouldBe(actorId);
        resolved.DisplayName.ShouldBe("Ada");
    }

    [Fact]
    public async Task UnregisterAsync_and_ResolveAsync_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var actorId = Guid.NewGuid();
        var address = new Address("agent", actorId);
        var entry = new DirectoryEntry(address, actorId, "Ada", "Backend engineer", null, DateTimeOffset.UtcNow);

        await _service.RegisterAsync(entry, ct);
        await _service.UnregisterAsync(address, ct);

        var resolved = await _service.ResolveAsync(address, ct);
        resolved.ShouldBeNull();
    }

    [Fact]
    public async Task UpdateEntryAsync_updates_displayName_and_description()
    {
        var ct = TestContext.Current.CancellationToken;
        var actorId = Guid.NewGuid();
        var address = new Address("unit", actorId);
        var entry = new DirectoryEntry(address, actorId, "old-display", "old-desc", null, DateTimeOffset.UtcNow);

        await _service.RegisterAsync(entry, ct);

        var updated = await _service.UpdateEntryAsync(address, "new-display", "new-desc", ct);

        updated.ShouldNotBeNull();
        updated!.DisplayName.ShouldBe("new-display");
        updated.Description.ShouldBe("new-desc");

        var resolved = await _service.ResolveAsync(address, ct);
        resolved!.DisplayName.ShouldBe("new-display");
        resolved.Description.ShouldBe("new-desc");
    }

    [Fact]
    public async Task UpdateEntryAsync_null_fields_leave_existing_values()
    {
        var ct = TestContext.Current.CancellationToken;
        var actorId = Guid.NewGuid();
        var address = new Address("unit", actorId);
        var entry = new DirectoryEntry(address, actorId, "display", "description", null, DateTimeOffset.UtcNow);

        await _service.RegisterAsync(entry, ct);

        var updated = await _service.UpdateEntryAsync(address, displayName: null, description: "new-desc", ct);

        updated.ShouldNotBeNull();
        updated!.DisplayName.ShouldBe("display");
        updated.Description.ShouldBe("new-desc");
    }

    [Fact]
    public async Task UpdateEntryAsync_unknown_address_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = new Address("unit", Guid.NewGuid());

        var updated = await _service.UpdateEntryAsync(address, "display", "desc", ct);

        updated.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveByRoleAsync_returns_matching_entries()
    {
        var ct = TestContext.Current.CancellationToken;
        var adaId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        var charlieId = Guid.NewGuid();
        var entry1 = new DirectoryEntry(
            new Address("agent", adaId), adaId, "Ada", "Engineer", "backend-engineer", DateTimeOffset.UtcNow);
        var entry2 = new DirectoryEntry(
            new Address("agent", bobId), bobId, "Bob", "Engineer", "backend-engineer", DateTimeOffset.UtcNow);
        var entry3 = new DirectoryEntry(
            new Address("agent", charlieId), charlieId, "Charlie", "Designer", "frontend-engineer", DateTimeOffset.UtcNow);

        await _service.RegisterAsync(entry1, ct);
        await _service.RegisterAsync(entry2, ct);
        await _service.RegisterAsync(entry3, ct);

        var results = await _service.ResolveByRoleAsync("backend-engineer", ct);

        results.Count().ShouldBe(2);
        results.Select(e => e.ActorId).ShouldBe(new[] { adaId, bobId }, ignoreOrder: true);
    }

    [Fact]
    public async Task RegisterAsync_persists_unit_to_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        var address = new Address("unit", unitId);
        var entry = new DirectoryEntry(address, unitId, "Engineering", "Engineering unit", null, DateTimeOffset.UtcNow);

        await _service.RegisterAsync(entry, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var entity = await db.UnitDefinitions.FirstOrDefaultAsync(u => u.Id == unitId, ct);

        entity.ShouldNotBeNull();
        entity!.Id.ShouldBe(unitId);
        entity.DisplayName.ShouldBe("Engineering");
        entity.Description.ShouldBe("Engineering unit");
    }

    [Fact]
    public async Task RegisterAsync_persists_agent_to_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        var address = new Address("agent", agentId);
        var entry = new DirectoryEntry(address, agentId, "Ada", "Backend engineer", "backend-engineer", DateTimeOffset.UtcNow);

        await _service.RegisterAsync(entry, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var entity = await db.AgentDefinitions.FirstOrDefaultAsync(a => a.Id == agentId, ct);

        entity.ShouldNotBeNull();
        entity!.Id.ShouldBe(agentId);
        entity.DisplayName.ShouldBe("Ada");
        entity.Role.ShouldBe("backend-engineer");
    }

    [Fact]
    public async Task ResolveAsync_cache_miss_loads_from_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var seededId = Guid.NewGuid();

        // Seed the database directly, bypassing the cache.
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.AgentDefinitions.Add(new AgentDefinitionEntity
            {
                Id = seededId,
                DisplayName = "Seeded Agent",
                Description = "Seeded via DB",
                Role = "tester",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        // A fresh DirectoryService instance has an empty cache.
        var freshService = new DirectoryService(
            new DirectoryCache(),
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _loggerFactory);

        var resolved = await freshService.ResolveAsync(new Address("agent", seededId), ct);

        resolved.ShouldNotBeNull();
        resolved!.ActorId.ShouldBe(seededId);
        resolved.DisplayName.ShouldBe("Seeded Agent");
        resolved.Role.ShouldBe("tester");
    }

    [Fact]
    public async Task UnregisterAsync_soft_deletes_from_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        var address = new Address("unit", unitId);
        var entry = new DirectoryEntry(address, unitId, "Remove Me", "Will be removed", null, DateTimeOffset.UtcNow);

        await _service.RegisterAsync(entry, ct);
        await _service.UnregisterAsync(address, ct);

        // Verify soft-deleted — not returned by normal query.
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var visible = await db.UnitDefinitions.FirstOrDefaultAsync(u => u.Id == unitId, ct);
        visible.ShouldBeNull();

        // But still exists with IgnoreQueryFilters.
        var softDeleted = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.Id == unitId, ct);
        softDeleted.ShouldNotBeNull();
        softDeleted!.DeletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task RegisterAsync_idempotent_upserts_existing_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var agentId = Guid.NewGuid();
        var address = new Address("agent", agentId);
        var entry1 = new DirectoryEntry(address, agentId, "V1", "First", "role-a", DateTimeOffset.UtcNow);
        var entry2 = new DirectoryEntry(address, agentId, "V2", "Second", "role-b", DateTimeOffset.UtcNow);

        await _service.RegisterAsync(entry1, ct);
        await _service.RegisterAsync(entry2, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var count = await db.AgentDefinitions.CountAsync(a => a.Id == agentId, ct);
        count.ShouldBe(1);

        var entity = await db.AgentDefinitions.FirstAsync(a => a.Id == agentId, ct);
        entity.DisplayName.ShouldBe("V2");
    }

    [Fact]
    public async Task RoundTrip_survives_service_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        var address = new Address("unit", unitId);
        var entry = new DirectoryEntry(address, unitId, "Persistent", "Survives restart", null, DateTimeOffset.UtcNow);

        // Register in the first service instance.
        await _service.RegisterAsync(entry, ct);

        // Create a brand-new DirectoryService (simulates container restart).
        var freshService = new DirectoryService(
            new DirectoryCache(),
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _loggerFactory);

        var resolved = await freshService.ResolveAsync(address, ct);

        resolved.ShouldNotBeNull();
        resolved!.ActorId.ShouldBe(unitId);
        resolved.DisplayName.ShouldBe("Persistent");
        resolved.Description.ShouldBe("Survives restart");
    }

    [Fact]
    public async Task ListAllAsync_loads_from_database_on_cold_cache()
    {
        var ct = TestContext.Current.CancellationToken;
        var seededId = Guid.NewGuid();

        // Seed the database directly.
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = seededId,
                DisplayName = "Seeded Unit",
                Description = "From DB",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync(ct);
        }

        var freshService = new DirectoryService(
            new DirectoryCache(),
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _loggerFactory);

        var all = await freshService.ListAllAsync(ct);

        all.ShouldContain(e => e.ActorId == seededId);
    }

    /// <summary>
    /// #652: deleting a unit must hard-delete every <c>UnitMembershipEntity</c>
    /// row referencing the unit. The table has no <c>DeletedAt</c> column so
    /// soft-delete is not representable — the invariant is "no row points at a
    /// deleted unit".
    /// </summary>
    [Fact]
    public async Task UnregisterAsync_unit_removes_all_memberships()
    {
        var unitEngId = Guid.NewGuid();

        var ct = TestContext.Current.CancellationToken;
        var proxyFactory = Substitute.For<IActorProxyFactory>();
        var service = CreateServiceWithActorFactory(proxyFactory);

        var unitAddress = new Address("unit", unitEngId);
        await service.RegisterAsync(
            new DirectoryEntry(unitAddress, unitEngId, "Engineering", "", null, DateTimeOffset.UtcNow),
            ct);

        StubUnitMembers(proxyFactory, unitEngId, Array.Empty<Address>());

        // Seed two memberships into this unit.
        var agentAda = Guid.NewGuid();
        var agentHopper = Guid.NewGuid();
        await SeedMembershipAsync(unitEngId, agentAda, ct);
        await SeedMembershipAsync(unitEngId, agentHopper, ct);

        await service.UnregisterAsync(unitAddress, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var remaining = await db.UnitMemberships
            .Where(m => m.UnitId == unitEngId)
            .CountAsync(ct);
        remaining.ShouldBe(0);
    }

    /// <summary>
    /// #652 ref-counting rule: when the unit being deleted was the agent's
    /// only membership, the agent is soft-deleted in the same cascade.
    /// </summary>
    [Fact]
    public async Task UnregisterAsync_unit_soft_deletes_exclusive_agent()
    {
        var unitEngId = Guid.NewGuid();
        var agentAdaId = Guid.NewGuid();

        var ct = TestContext.Current.CancellationToken;
        var proxyFactory = Substitute.For<IActorProxyFactory>();
        var service = CreateServiceWithActorFactory(proxyFactory);

        var unitAddress = new Address("unit", unitEngId);
        await service.RegisterAsync(
            new DirectoryEntry(unitAddress, unitEngId, "Engineering", "", null, DateTimeOffset.UtcNow),
            ct);
        await service.RegisterAsync(
            new DirectoryEntry(new Address("agent", agentAdaId), agentAdaId, "Ada", "", "engineer", DateTimeOffset.UtcNow),
            ct);

        StubUnitMembers(proxyFactory, unitEngId, Array.Empty<Address>());
        await SeedMembershipAsync(unitEngId, agentAdaId, ct);

        await service.UnregisterAsync(unitAddress, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var agent = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(a => a.Id == agentAdaId, ct);
        agent.DeletedAt.ShouldNotBeNull();
    }

    /// <summary>
    /// #652 ref-counting rule: when the agent still has at least one live
    /// membership in a different unit, only the edge is removed. The agent
    /// itself must survive.
    /// </summary>
    [Fact]
    public async Task UnregisterAsync_unit_preserves_shared_agent()
    {
        var unitXId = Guid.NewGuid();
        var unitYId = Guid.NewGuid();
        var agentAdaId = Guid.NewGuid();

        var ct = TestContext.Current.CancellationToken;
        var proxyFactory = Substitute.For<IActorProxyFactory>();
        var service = CreateServiceWithActorFactory(proxyFactory);

        var unitX = new Address("unit", unitXId);
        var unitY = new Address("unit", unitYId);
        await service.RegisterAsync(
            new DirectoryEntry(unitX, unitXId, "X", "", null, DateTimeOffset.UtcNow), ct);
        await service.RegisterAsync(
            new DirectoryEntry(unitY, unitYId, "Y", "", null, DateTimeOffset.UtcNow), ct);
        await service.RegisterAsync(
            new DirectoryEntry(new Address("agent", agentAdaId), agentAdaId, "Ada", "", null, DateTimeOffset.UtcNow), ct);

        StubUnitMembers(proxyFactory, unitXId, Array.Empty<Address>());
        await SeedMembershipAsync(unitXId, agentAdaId, ct);
        await SeedMembershipAsync(unitYId, agentAdaId, ct);

        await service.UnregisterAsync(unitX, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // Edge into X is gone.
        (await db.UnitMemberships.AnyAsync(m => m.UnitId == unitXId && m.AgentId == agentAdaId, ct))
            .ShouldBeFalse();
        // Edge into Y is preserved.
        (await db.UnitMemberships.AnyAsync(m => m.UnitId == unitYId && m.AgentId == agentAdaId, ct))
            .ShouldBeTrue();
        // Agent itself survives.
        var agent = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(a => a.Id == agentAdaId, ct);
        agent.DeletedAt.ShouldBeNull();
    }

    /// <summary>
    /// #652: sub-units cascade. Deleting parent P with sub-unit S (S owns an
    /// exclusive agent) must soft-delete P, S, and S's agent; S's membership
    /// row is also cleaned up.
    /// </summary>
    [Fact]
    public async Task UnregisterAsync_unit_cascades_sub_units()
    {
        var unitPId = Guid.NewGuid();
        var unitSId = Guid.NewGuid();
        var agentExclusiveId = Guid.NewGuid();

        var ct = TestContext.Current.CancellationToken;
        var proxyFactory = Substitute.For<IActorProxyFactory>();
        var service = CreateServiceWithActorFactory(proxyFactory);

        var parent = new Address("unit", unitPId);
        var sub = new Address("unit", unitSId);
        await service.RegisterAsync(
            new DirectoryEntry(parent, unitPId, "P", "", null, DateTimeOffset.UtcNow), ct);
        await service.RegisterAsync(
            new DirectoryEntry(sub, unitSId, "S", "", null, DateTimeOffset.UtcNow), ct);
        await service.RegisterAsync(
            new DirectoryEntry(new Address("agent", agentExclusiveId), agentExclusiveId, "Ex", "", null, DateTimeOffset.UtcNow),
            ct);

        // Parent lists sub as a unit-typed member; sub has no further nesting.
        StubUnitMembers(proxyFactory, unitPId, new[] { sub });
        StubUnitMembers(proxyFactory, unitSId, Array.Empty<Address>());

        await SeedMembershipAsync(unitSId, agentExclusiveId, ct);

        await service.UnregisterAsync(parent, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var parentEntity = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(u => u.Id == unitPId, ct);
        parentEntity.DeletedAt.ShouldNotBeNull();

        var subEntity = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(u => u.Id == unitSId, ct);
        subEntity.DeletedAt.ShouldNotBeNull();

        var agent = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(a => a.Id == agentExclusiveId, ct);
        agent.DeletedAt.ShouldNotBeNull();

        (await db.UnitMemberships.CountAsync(m => m.UnitId == unitSId, ct)).ShouldBe(0);
    }

    /// <summary>
    /// #652: when a sub-unit's agent is also a member of an unrelated live
    /// unit, the sub-unit itself is soft-deleted but the shared agent
    /// survives with its surviving membership intact.
    /// </summary>
    [Fact]
    public async Task UnregisterAsync_unit_cascades_sub_unit_but_preserves_shared_agent()
    {
        var unitPId = Guid.NewGuid();
        var unitSId = Guid.NewGuid();
        var unitUId = Guid.NewGuid();
        var agentSharedId = Guid.NewGuid();

        var ct = TestContext.Current.CancellationToken;
        var proxyFactory = Substitute.For<IActorProxyFactory>();
        var service = CreateServiceWithActorFactory(proxyFactory);

        var parent = new Address("unit", unitPId);
        var sub = new Address("unit", unitSId);
        var unrelated = new Address("unit", unitUId);
        await service.RegisterAsync(
            new DirectoryEntry(parent, unitPId, "P", "", null, DateTimeOffset.UtcNow), ct);
        await service.RegisterAsync(
            new DirectoryEntry(sub, unitSId, "S", "", null, DateTimeOffset.UtcNow), ct);
        await service.RegisterAsync(
            new DirectoryEntry(unrelated, unitUId, "U", "", null, DateTimeOffset.UtcNow), ct);
        await service.RegisterAsync(
            new DirectoryEntry(new Address("agent", agentSharedId), agentSharedId, "Sh", "", null, DateTimeOffset.UtcNow),
            ct);

        StubUnitMembers(proxyFactory, unitPId, new[] { sub });
        StubUnitMembers(proxyFactory, unitSId, Array.Empty<Address>());

        await SeedMembershipAsync(unitSId, agentSharedId, ct);
        await SeedMembershipAsync(unitUId, agentSharedId, ct);

        await service.UnregisterAsync(parent, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // Sub-unit is soft-deleted.
        var subEntity = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(u => u.Id == unitSId, ct);
        subEntity.DeletedAt.ShouldNotBeNull();

        // Unrelated live unit untouched.
        var unrelatedEntity = await db.UnitDefinitions
            .FirstAsync(u => u.Id == unitUId, ct);
        unrelatedEntity.DeletedAt.ShouldBeNull();

        // Shared agent survives.
        var agent = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(a => a.Id == agentSharedId, ct);
        agent.DeletedAt.ShouldBeNull();

        // Only the membership into U remains.
        var remaining = await db.UnitMemberships
            .Where(m => m.AgentId == agentSharedId)
            .Select(m => m.UnitId)
            .ToListAsync(ct);
        remaining.ShouldBe(new[] { unitUId });
    }

    /// <summary>
    /// #652: unregistering a unit whose row is already soft-deleted is a
    /// no-op — matches the pre-existing "not found" behaviour of
    /// <c>DeleteEntryAsync</c> which silently returns when the entity is
    /// missing. No second <c>DeletedAt</c> stamp is applied.
    /// </summary>
    [Fact]
    public async Task UnregisterAsync_already_deleted_unit_is_noop()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxyFactory = Substitute.For<IActorProxyFactory>();
        var service = CreateServiceWithActorFactory(proxyFactory);

        var ghostId = Guid.NewGuid();
        var unitAddress = new Address("unit", ghostId);
        await service.RegisterAsync(
            new DirectoryEntry(unitAddress, ghostId, "Ghost", "", null, DateTimeOffset.UtcNow),
            ct);

        StubUnitMembers(proxyFactory, ghostId, Array.Empty<Address>());

        await service.UnregisterAsync(unitAddress, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var first = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(u => u.Id == ghostId, ct);
        var firstStamp = first.DeletedAt;
        firstStamp.ShouldNotBeNull();

        // Second call should not re-stamp or throw.
        await service.UnregisterAsync(unitAddress, ct);

        var second = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(u => u.Id == ghostId, ct);
        second.DeletedAt.ShouldBe(firstStamp);
    }

    /// <summary>
    /// #1135 regression: deleting a unit must make it disappear from
    /// <see cref="DirectoryService.ResolveAsync"/> in the same process, with
    /// no restart.
    /// </summary>
    [Fact]
    public async Task UnregisterAsync_unit_in_process_resolve_returns_null_after_cascade()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxyFactory = Substitute.For<IActorProxyFactory>();
        var service = CreateServiceWithActorFactory(proxyFactory);

        var ghostId = Guid.NewGuid();
        var unitAddress = new Address("unit", ghostId);
        await service.RegisterAsync(
            new DirectoryEntry(unitAddress, ghostId, "Ghost", "", null, DateTimeOffset.UtcNow),
            ct);

        StubUnitMembers(proxyFactory, ghostId, Array.Empty<Address>());

        await service.UnregisterAsync(unitAddress, ct);

        // The same process that just deleted the row must not serve a
        // cached/repopulated entry for it.
        var resolved = await service.ResolveAsync(unitAddress, ct);
        resolved.ShouldBeNull();
    }

    /// <summary>
    /// #1135 regression: <see cref="DirectoryService.ListAllAsync"/> must
    /// not include the deleted unit immediately after
    /// <see cref="DirectoryService.UnregisterAsync"/> returns.
    /// </summary>
    [Fact]
    public async Task UnregisterAsync_unit_in_process_list_does_not_include_after_cascade()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxyFactory = Substitute.For<IActorProxyFactory>();
        var service = CreateServiceWithActorFactory(proxyFactory);

        var ghostId = Guid.NewGuid();
        var unitAddress = new Address("unit", ghostId);
        await service.RegisterAsync(
            new DirectoryEntry(unitAddress, ghostId, "Ghost", "", null, DateTimeOffset.UtcNow),
            ct);

        StubUnitMembers(proxyFactory, ghostId, Array.Empty<Address>());

        // Warm the cache via the same path the API uses on every list.
        var beforeDelete = await service.ListAllAsync(ct);
        beforeDelete.ShouldContain(e => e.ActorId == ghostId);

        await service.UnregisterAsync(unitAddress, ct);

        var afterDelete = await service.ListAllAsync(ct);
        afterDelete.ShouldNotContain(e => e.ActorId == ghostId);
    }

    /// <summary>
    /// #1135 regression: the cascade-through-sub-units path must also leave
    /// the in-memory state coherent.
    /// </summary>
    [Fact]
    public async Task UnregisterAsync_unit_cascades_in_process_eviction()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxyFactory = Substitute.For<IActorProxyFactory>();
        var service = CreateServiceWithActorFactory(proxyFactory);

        var parentId = Guid.NewGuid();
        var subId = Guid.NewGuid();
        var parent = new Address("unit", parentId);
        var sub = new Address("unit", subId);
        await service.RegisterAsync(
            new DirectoryEntry(parent, parentId, "P", "", null, DateTimeOffset.UtcNow), ct);
        await service.RegisterAsync(
            new DirectoryEntry(sub, subId, "S", "", null, DateTimeOffset.UtcNow), ct);

        StubUnitMembers(proxyFactory, parentId, new[] { sub });
        StubUnitMembers(proxyFactory, subId, Array.Empty<Address>());

        await service.UnregisterAsync(parent, ct);

        (await service.ResolveAsync(parent, ct)).ShouldBeNull();
        (await service.ResolveAsync(sub, ct)).ShouldBeNull();

        var listed = await service.ListAllAsync(ct);
        listed.ShouldNotContain(e => e.ActorId == parentId);
        listed.ShouldNotContain(e => e.ActorId == subId);
    }

    private DirectoryService CreateServiceWithActorFactory(IActorProxyFactory proxyFactory)
    {
        return new DirectoryService(
            _cache,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _loggerFactory,
            proxyFactory);
    }

    private static void StubUnitMembers(
        IActorProxyFactory factory, Guid actorId, Address[] members)
    {
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetMembersAsync(Arg.Any<CancellationToken>()).Returns(members);
        var actorIdString = GuidFormatter.Format(actorId);
        factory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == actorIdString),
                Arg.Any<string>())
            .Returns(proxy);
    }

    private async Task SeedMembershipAsync(Guid unitId, Guid agentId, CancellationToken ct)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.UnitMemberships.Add(new UnitMembershipEntity
        {
            UnitId = unitId,
            AgentId = agentId,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync(ct);
    }
}