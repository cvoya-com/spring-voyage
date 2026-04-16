// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Routing;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Routing;

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
}