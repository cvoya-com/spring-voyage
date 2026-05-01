// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Routing;

using Cvoya.Spring.Core.Directory;
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
        var address = new Address("agent", "engineering-team/ada");
        var entry = new DirectoryEntry(address, "actor-1", "Ada", "Backend engineer", "backend-engineer", DateTimeOffset.UtcNow);

        await _service.RegisterAsync(entry, ct);

        var resolved = await _service.ResolveAsync(address, ct);

        resolved.ShouldNotBeNull();
        resolved!.ActorId.ShouldBe("actor-1");
        resolved.DisplayName.ShouldBe("Ada");
    }

    [Fact]
    public async Task UnregisterAsync_and_ResolveAsync_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = new Address("agent", "engineering-team/ada");
        var entry = new DirectoryEntry(address, "actor-1", "Ada", "Backend engineer", null, DateTimeOffset.UtcNow);

        await _service.RegisterAsync(entry, ct);
        await _service.UnregisterAsync(address, ct);

        var resolved = await _service.ResolveAsync(address, ct);
        resolved.ShouldBeNull();
    }

    [Fact]
    public async Task UpdateEntryAsync_updates_displayName_and_description()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = new Address("unit", "engineering");
        var entry = new DirectoryEntry(address, "actor-1", "old-display", "old-desc", null, DateTimeOffset.UtcNow);

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
        var address = new Address("unit", "engineering");
        var entry = new DirectoryEntry(address, "actor-1", "display", "description", null, DateTimeOffset.UtcNow);

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
        var address = new Address("unit", "missing");

        var updated = await _service.UpdateEntryAsync(address, "display", "desc", ct);

        updated.ShouldBeNull();
    }

    [Fact]
    public async Task ResolveByRoleAsync_returns_matching_entries()
    {
        var ct = TestContext.Current.CancellationToken;
        var entry1 = new DirectoryEntry(
            new Address("agent", "team/ada"), "actor-1", "Ada", "Engineer", "backend-engineer", DateTimeOffset.UtcNow);
        var entry2 = new DirectoryEntry(
            new Address("agent", "team/bob"), "actor-2", "Bob", "Engineer", "backend-engineer", DateTimeOffset.UtcNow);
        var entry3 = new DirectoryEntry(
            new Address("agent", "team/charlie"), "actor-3", "Charlie", "Designer", "frontend-engineer", DateTimeOffset.UtcNow);

        await _service.RegisterAsync(entry1, ct);
        await _service.RegisterAsync(entry2, ct);
        await _service.RegisterAsync(entry3, ct);

        var results = await _service.ResolveByRoleAsync("backend-engineer", ct);

        results.Count().ShouldBe(2);
        results.Select(e => e.ActorId).ShouldBe(new[] { "actor-1", "actor-2" }, ignoreOrder: true);
    }

    [Fact]
    public async Task RegisterAsync_persists_unit_to_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = new Address("unit", "engineering");
        var entry = new DirectoryEntry(address, "unit-actor-1", "Engineering", "Engineering unit", null, DateTimeOffset.UtcNow);

        await _service.RegisterAsync(entry, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var entity = await db.UnitDefinitions.FirstOrDefaultAsync(u => u.UnitId == "engineering", ct);

        entity.ShouldNotBeNull();
        entity!.ActorId.ShouldBe("unit-actor-1");
        entity.Name.ShouldBe("Engineering");
        entity.Description.ShouldBe("Engineering unit");
    }

    [Fact]
    public async Task RegisterAsync_persists_agent_to_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = new Address("agent", "team/ada");
        var entry = new DirectoryEntry(address, "agent-actor-1", "Ada", "Backend engineer", "backend-engineer", DateTimeOffset.UtcNow);

        await _service.RegisterAsync(entry, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var entity = await db.AgentDefinitions.FirstOrDefaultAsync(a => a.AgentId == "team/ada", ct);

        entity.ShouldNotBeNull();
        entity!.ActorId.ShouldBe("agent-actor-1");
        entity.Name.ShouldBe("Ada");
        entity.Role.ShouldBe("backend-engineer");
    }

    [Fact]
    public async Task ResolveAsync_cache_miss_loads_from_database()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed the database directly, bypassing the cache.
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.AgentDefinitions.Add(new AgentDefinitionEntity
            {
                Id = Guid.NewGuid(),
                AgentId = "team/seeded",
                ActorId = "seeded-actor",
                Name = "Seeded Agent",
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

        var resolved = await freshService.ResolveAsync(new Address("agent", "team/seeded"), ct);

        resolved.ShouldNotBeNull();
        resolved!.ActorId.ShouldBe("seeded-actor");
        resolved.DisplayName.ShouldBe("Seeded Agent");
        resolved.Role.ShouldBe("tester");
    }

    [Fact]
    public async Task UnregisterAsync_soft_deletes_from_database()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = new Address("unit", "to-remove");
        var entry = new DirectoryEntry(address, "actor-rm", "Remove Me", "Will be removed", null, DateTimeOffset.UtcNow);

        await _service.RegisterAsync(entry, ct);
        await _service.UnregisterAsync(address, ct);

        // Verify soft-deleted — not returned by normal query.
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var visible = await db.UnitDefinitions.FirstOrDefaultAsync(u => u.UnitId == "to-remove", ct);
        visible.ShouldBeNull();

        // But still exists with IgnoreQueryFilters.
        var softDeleted = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(u => u.UnitId == "to-remove", ct);
        softDeleted.ShouldNotBeNull();
        softDeleted!.DeletedAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task RegisterAsync_idempotent_upserts_existing_row()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = new Address("agent", "team/idempotent");
        var entry1 = new DirectoryEntry(address, "actor-v1", "V1", "First", "role-a", DateTimeOffset.UtcNow);
        var entry2 = new DirectoryEntry(address, "actor-v2", "V2", "Second", "role-b", DateTimeOffset.UtcNow);

        await _service.RegisterAsync(entry1, ct);
        await _service.RegisterAsync(entry2, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var count = await db.AgentDefinitions.CountAsync(a => a.AgentId == "team/idempotent", ct);
        count.ShouldBe(1);

        var entity = await db.AgentDefinitions.FirstAsync(a => a.AgentId == "team/idempotent", ct);
        entity.ActorId.ShouldBe("actor-v2");
        entity.Name.ShouldBe("V2");
    }

    [Fact]
    public async Task RoundTrip_survives_service_restart()
    {
        var ct = TestContext.Current.CancellationToken;
        var address = new Address("unit", "persistent-unit");
        var entry = new DirectoryEntry(address, "unit-actor-99", "Persistent", "Survives restart", null, DateTimeOffset.UtcNow);

        // Register in the first service instance.
        await _service.RegisterAsync(entry, ct);

        // Create a brand-new DirectoryService (simulates container restart).
        var freshService = new DirectoryService(
            new DirectoryCache(),
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _loggerFactory);

        var resolved = await freshService.ResolveAsync(address, ct);

        resolved.ShouldNotBeNull();
        resolved!.ActorId.ShouldBe("unit-actor-99");
        resolved.DisplayName.ShouldBe("Persistent");
        resolved.Description.ShouldBe("Survives restart");
    }

    [Fact]
    public async Task ListAllAsync_loads_from_database_on_cold_cache()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed the database directly.
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            db.UnitDefinitions.Add(new UnitDefinitionEntity
            {
                Id = Guid.NewGuid(),
                UnitId = "seeded-unit",
                ActorId = "seeded-unit-actor",
                Name = "Seeded Unit",
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

        all.ShouldContain(e => e.Address.Path == "seeded-unit");
    }

    /// <summary>
    /// #652: deleting a unit must hard-delete every <c>UnitMembershipEntity</c>
    /// row referencing the unit. The table has no <c>DeletedAt</c> column so
    /// soft-delete is not representable — the invariant is "no row points at a
    /// deleted unit".
    ///
    /// Post #1492 the membership rows are keyed by stable UUIDs (actor IDs), so
    /// all RegisterAsync calls in cascade tests use UUID-shaped ActorId values.
    /// </summary>
    [Fact]
    public async Task UnregisterAsync_unit_removes_all_memberships()
    {
        // Stable UUIDs: must be valid Guid strings for cascade path to parse.
        var unitEngUuid = new Guid("eeee0001-0000-0000-0000-000000000000");

        var ct = TestContext.Current.CancellationToken;
        var proxyFactory = Substitute.For<IActorProxyFactory>();
        var service = CreateServiceWithActorFactory(proxyFactory);

        var unitAddress = new Address("unit", "engineering");
        await service.RegisterAsync(
            new DirectoryEntry(unitAddress, unitEngUuid.ToString(), "Engineering", "", null, DateTimeOffset.UtcNow),
            ct);

        StubUnitMembers(proxyFactory, unitEngUuid.ToString(), Array.Empty<Address>());

        // Seed two memberships into this unit (using deterministic agent UUIDs).
        var agentAda = new Guid("aaaa0010-0000-0000-0000-000000000000");
        var agentHopper = new Guid("aaaa0011-0000-0000-0000-000000000000");
        await SeedMembershipAsync(unitEngUuid, agentAda, ct);
        await SeedMembershipAsync(unitEngUuid, agentHopper, ct);

        await service.UnregisterAsync(unitAddress, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var remaining = await db.UnitMemberships
            .Where(m => m.UnitId == unitEngUuid)
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
        var unitEngUuid = new Guid("eeee0002-0000-0000-0000-000000000000");
        var agentAdaUuid = new Guid("aaaa0001-0000-0000-0000-000000000000");

        var ct = TestContext.Current.CancellationToken;
        var proxyFactory = Substitute.For<IActorProxyFactory>();
        var service = CreateServiceWithActorFactory(proxyFactory);

        var unitAddress = new Address("unit", "engineering");
        await service.RegisterAsync(
            new DirectoryEntry(unitAddress, unitEngUuid.ToString(), "Engineering", "", null, DateTimeOffset.UtcNow),
            ct);
        await service.RegisterAsync(
            new DirectoryEntry(new Address("agent", "ada"), agentAdaUuid.ToString(), "Ada", "", "engineer", DateTimeOffset.UtcNow),
            ct);

        StubUnitMembers(proxyFactory, unitEngUuid.ToString(), Array.Empty<Address>());
        await SeedMembershipAsync(unitEngUuid, agentAdaUuid, ct);

        await service.UnregisterAsync(unitAddress, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var agent = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(a => a.AgentId == "ada", ct);
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
        var unitXUuid = new Guid("aaaa0001-0000-0000-0000-000000000001");
        var unitYUuid = new Guid("aaaa0001-0000-0000-0000-000000000002");
        var agentAdaUuid = new Guid("aaaa0002-0000-0000-0000-000000000000");

        var ct = TestContext.Current.CancellationToken;
        var proxyFactory = Substitute.For<IActorProxyFactory>();
        var service = CreateServiceWithActorFactory(proxyFactory);

        var unitX = new Address("unit", "x");
        var unitY = new Address("unit", "y");
        await service.RegisterAsync(
            new DirectoryEntry(unitX, unitXUuid.ToString(), "X", "", null, DateTimeOffset.UtcNow), ct);
        await service.RegisterAsync(
            new DirectoryEntry(unitY, unitYUuid.ToString(), "Y", "", null, DateTimeOffset.UtcNow), ct);
        await service.RegisterAsync(
            new DirectoryEntry(new Address("agent", "ada"), agentAdaUuid.ToString(), "Ada", "", null, DateTimeOffset.UtcNow), ct);

        StubUnitMembers(proxyFactory, unitXUuid.ToString(), Array.Empty<Address>());
        await SeedMembershipAsync(unitXUuid, agentAdaUuid, ct);
        await SeedMembershipAsync(unitYUuid, agentAdaUuid, ct);

        await service.UnregisterAsync(unitX, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // Edge into X is gone.
        (await db.UnitMemberships.AnyAsync(m => m.UnitId == unitXUuid && m.AgentId == agentAdaUuid, ct))
            .ShouldBeFalse();
        // Edge into Y is preserved.
        (await db.UnitMemberships.AnyAsync(m => m.UnitId == unitYUuid && m.AgentId == agentAdaUuid, ct))
            .ShouldBeTrue();
        // Agent itself survives.
        var agent = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(a => a.AgentId == "ada", ct);
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
        var unitPUuid = new Guid("bbbb0001-0000-0000-0000-000000000001");
        var unitSUuid = new Guid("bbbb0001-0000-0000-0000-000000000002");
        var agentExclusiveUuid = new Guid("eeeeeeee-0001-0000-0000-000000000000");

        var ct = TestContext.Current.CancellationToken;
        var proxyFactory = Substitute.For<IActorProxyFactory>();
        var service = CreateServiceWithActorFactory(proxyFactory);

        var parent = new Address("unit", "p");
        var sub = new Address("unit", "s");
        await service.RegisterAsync(
            new DirectoryEntry(parent, unitPUuid.ToString(), "P", "", null, DateTimeOffset.UtcNow), ct);
        await service.RegisterAsync(
            new DirectoryEntry(sub, unitSUuid.ToString(), "S", "", null, DateTimeOffset.UtcNow), ct);
        await service.RegisterAsync(
            new DirectoryEntry(new Address("agent", "exclusive"), agentExclusiveUuid.ToString(), "Ex", "", null, DateTimeOffset.UtcNow),
            ct);

        // Parent lists sub as a unit-typed member; sub has no further nesting.
        StubUnitMembers(proxyFactory, unitPUuid.ToString(), new[] { sub });
        StubUnitMembers(proxyFactory, unitSUuid.ToString(), Array.Empty<Address>());

        await SeedMembershipAsync(unitSUuid, agentExclusiveUuid, ct);

        await service.UnregisterAsync(parent, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var parentEntity = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(u => u.UnitId == "p", ct);
        parentEntity.DeletedAt.ShouldNotBeNull();

        var subEntity = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(u => u.UnitId == "s", ct);
        subEntity.DeletedAt.ShouldNotBeNull();

        var agent = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(a => a.AgentId == "exclusive", ct);
        agent.DeletedAt.ShouldNotBeNull();

        (await db.UnitMemberships.CountAsync(m => m.UnitId == unitSUuid, ct)).ShouldBe(0);
    }

    /// <summary>
    /// #652: when a sub-unit's agent is also a member of an unrelated live
    /// unit, the sub-unit itself is soft-deleted but the shared agent
    /// survives with its surviving membership intact.
    /// </summary>
    [Fact]
    public async Task UnregisterAsync_unit_cascades_sub_unit_but_preserves_shared_agent()
    {
        var unitPUuid = new Guid("cccc0001-0000-0000-0000-000000000001");
        var unitSUuid = new Guid("cccc0001-0000-0000-0000-000000000002");
        var unitUUuid = new Guid("cccc0001-0000-0000-0000-000000000003");
        var agentSharedUuid = new Guid("aaaa0003-0000-0000-0000-000000000000");

        var ct = TestContext.Current.CancellationToken;
        var proxyFactory = Substitute.For<IActorProxyFactory>();
        var service = CreateServiceWithActorFactory(proxyFactory);

        var parent = new Address("unit", "p");
        var sub = new Address("unit", "s");
        var unrelated = new Address("unit", "u");
        await service.RegisterAsync(
            new DirectoryEntry(parent, unitPUuid.ToString(), "P", "", null, DateTimeOffset.UtcNow), ct);
        await service.RegisterAsync(
            new DirectoryEntry(sub, unitSUuid.ToString(), "S", "", null, DateTimeOffset.UtcNow), ct);
        await service.RegisterAsync(
            new DirectoryEntry(unrelated, unitUUuid.ToString(), "U", "", null, DateTimeOffset.UtcNow), ct);
        await service.RegisterAsync(
            new DirectoryEntry(new Address("agent", "shared"), agentSharedUuid.ToString(), "Sh", "", null, DateTimeOffset.UtcNow),
            ct);

        StubUnitMembers(proxyFactory, unitPUuid.ToString(), new[] { sub });
        StubUnitMembers(proxyFactory, unitSUuid.ToString(), Array.Empty<Address>());

        await SeedMembershipAsync(unitSUuid, agentSharedUuid, ct);
        await SeedMembershipAsync(unitUUuid, agentSharedUuid, ct);

        await service.UnregisterAsync(parent, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // Sub-unit is soft-deleted.
        var subEntity = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(u => u.UnitId == "s", ct);
        subEntity.DeletedAt.ShouldNotBeNull();

        // Unrelated live unit untouched.
        var unrelatedEntity = await db.UnitDefinitions
            .FirstAsync(u => u.UnitId == "u", ct);
        unrelatedEntity.DeletedAt.ShouldBeNull();

        // Shared agent survives.
        var agent = await db.AgentDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(a => a.AgentId == "shared", ct);
        agent.DeletedAt.ShouldBeNull();

        // Only the membership into U remains.
        var remaining = await db.UnitMemberships
            .Where(m => m.AgentId == agentSharedUuid)
            .Select(m => m.UnitId)
            .ToListAsync(ct);
        remaining.ShouldBe(new[] { unitUUuid });
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

        var unitAddress = new Address("unit", "ghost");
        await service.RegisterAsync(
            new DirectoryEntry(unitAddress, "unit-actor-ghost", "Ghost", "", null, DateTimeOffset.UtcNow),
            ct);

        StubUnitMembers(proxyFactory, "unit-actor-ghost", Array.Empty<Address>());

        await service.UnregisterAsync(unitAddress, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var first = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(u => u.UnitId == "ghost", ct);
        var firstStamp = first.DeletedAt;
        firstStamp.ShouldNotBeNull();

        // Second call should not re-stamp or throw.
        await service.UnregisterAsync(unitAddress, ct);

        var second = await db.UnitDefinitions
            .IgnoreQueryFilters()
            .FirstAsync(u => u.UnitId == "ghost", ct);
        second.DeletedAt.ShouldBe(firstStamp);
    }

    /// <summary>
    /// #1135 regression: deleting a unit must make it disappear from
    /// <see cref="DirectoryService.ResolveAsync"/> in the same process, with
    /// no restart. The previous implementation's cascade went through
    /// <see cref="DirectoryService.ResolveAsync"/> while computing sub-unit
    /// members, which write-through-repopulated <c>_entries</c> and the
    /// shared cache from the still-live DB row before the soft-delete
    /// stamp was applied. The post-delete in-memory state then served a
    /// ghost entry until the host restarted.
    /// </summary>
    [Fact]
    public async Task UnregisterAsync_unit_in_process_resolve_returns_null_after_cascade()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxyFactory = Substitute.For<IActorProxyFactory>();
        var service = CreateServiceWithActorFactory(proxyFactory);

        var unitAddress = new Address("unit", "ghost-resolve");
        await service.RegisterAsync(
            new DirectoryEntry(unitAddress, "unit-actor-ghost", "Ghost", "", null, DateTimeOffset.UtcNow),
            ct);

        StubUnitMembers(proxyFactory, "unit-actor-ghost", Array.Empty<Address>());

        await service.UnregisterAsync(unitAddress, ct);

        // The same process that just deleted the row must not serve a
        // cached/repopulated entry for it.
        var resolved = await service.ResolveAsync(unitAddress, ct);
        resolved.ShouldBeNull();
    }

    /// <summary>
    /// #1135 regression: <see cref="DirectoryService.ListAllAsync"/> must
    /// not include the deleted unit immediately after
    /// <see cref="DirectoryService.UnregisterAsync"/> returns. Same root
    /// cause as the resolve regression above; this is the
    /// <c>GET /api/v1/units</c> path.
    /// </summary>
    [Fact]
    public async Task UnregisterAsync_unit_in_process_list_does_not_include_after_cascade()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxyFactory = Substitute.For<IActorProxyFactory>();
        var service = CreateServiceWithActorFactory(proxyFactory);

        var unitAddress = new Address("unit", "ghost-list");
        await service.RegisterAsync(
            new DirectoryEntry(unitAddress, "unit-actor-ghost-list", "Ghost", "", null, DateTimeOffset.UtcNow),
            ct);

        StubUnitMembers(proxyFactory, "unit-actor-ghost-list", Array.Empty<Address>());

        // Warm the cache via the same path the API uses on every list.
        var beforeDelete = await service.ListAllAsync(ct);
        beforeDelete.ShouldContain(e => e.Address.Path == "ghost-list");

        await service.UnregisterAsync(unitAddress, ct);

        var afterDelete = await service.ListAllAsync(ct);
        afterDelete.ShouldNotContain(e => e.Address.Path == "ghost-list");
    }

    /// <summary>
    /// #1135 regression: the cascade-through-sub-units path must also leave
    /// the in-memory state coherent. Both the parent and the soft-deleted
    /// sub-unit must be invisible to a same-process resolve / list, even
    /// though the cascade walked through the parent's actor proxy to find
    /// the sub-unit and could trigger write-through repopulation along the
    /// way.
    /// </summary>
    [Fact]
    public async Task UnregisterAsync_unit_cascades_in_process_eviction()
    {
        var ct = TestContext.Current.CancellationToken;
        var proxyFactory = Substitute.For<IActorProxyFactory>();
        var service = CreateServiceWithActorFactory(proxyFactory);

        var parent = new Address("unit", "ghost-parent");
        var sub = new Address("unit", "ghost-sub");
        await service.RegisterAsync(
            new DirectoryEntry(parent, "unit-actor-ghost-parent", "P", "", null, DateTimeOffset.UtcNow), ct);
        await service.RegisterAsync(
            new DirectoryEntry(sub, "unit-actor-ghost-sub", "S", "", null, DateTimeOffset.UtcNow), ct);

        StubUnitMembers(proxyFactory, "unit-actor-ghost-parent", new[] { sub });
        StubUnitMembers(proxyFactory, "unit-actor-ghost-sub", Array.Empty<Address>());

        await service.UnregisterAsync(parent, ct);

        (await service.ResolveAsync(parent, ct)).ShouldBeNull();
        (await service.ResolveAsync(sub, ct)).ShouldBeNull();

        var listed = await service.ListAllAsync(ct);
        listed.ShouldNotContain(e => e.Address.Path == "ghost-parent");
        listed.ShouldNotContain(e => e.Address.Path == "ghost-sub");
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
        IActorProxyFactory factory, string actorId, Address[] members)
    {
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetMembersAsync(Arg.Any<CancellationToken>()).Returns(members);
        factory.CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(a => a.GetId() == actorId),
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