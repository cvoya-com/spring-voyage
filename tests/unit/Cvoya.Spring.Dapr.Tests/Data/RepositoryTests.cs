// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Data;

using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="Repository{T}"/> verifying CRUD operations and soft-delete behavior.
/// </summary>
public class RepositoryTests : IDisposable
{
    private readonly SpringDbContext _context;
    private readonly Repository<AgentDefinitionEntity> _repository;

    public RepositoryTests()
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new SpringDbContext(options);
        _repository = new Repository<AgentDefinitionEntity>(_context);
    }

    [Fact]
    public async Task GetByIdAsync_ExistingEntity_ReturnsEntity()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "Test Agent"
        };

        await _repository.CreateAsync(agent, ct);

        var result = await _repository.GetByIdAsync(agent.Id, ct);

        result.ShouldNotBeNull();
        result!.DisplayName.ShouldBe("Test Agent");
    }

    [Fact]
    public async Task GetByIdAsync_NonExistentId_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _repository.GetByIdAsync(Guid.NewGuid(), ct);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task GetAllAsync_MultipleEntities_ReturnsAllNonDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent1 = new AgentDefinitionEntity { Id = Guid.NewGuid(), DisplayName = "Agent 1" };
        var agent2 = new AgentDefinitionEntity { Id = Guid.NewGuid(), DisplayName = "Agent 2" };
        var deleted = new AgentDefinitionEntity { Id = Guid.NewGuid(), DisplayName = "Deleted", DeletedAt = DateTimeOffset.UtcNow };

        await _repository.CreateAsync(agent1, ct);
        await _repository.CreateAsync(agent2, ct);
        await _repository.CreateAsync(deleted, ct);

        var results = await _repository.GetAllAsync(ct);

        results.Count().ShouldBe(2);
        results.ShouldContain(a => a.DisplayName == "Agent 1");
        results.ShouldContain(a => a.DisplayName == "Agent 2");
    }

    [Fact]
    public async Task DeleteAsync_ExistingEntity_SetsSoftDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "To Delete"
        };

        await _repository.CreateAsync(agent, ct);
        await _repository.DeleteAsync(agent.Id, ct);

        // The soft-deleted entity should not appear in filtered queries.
        var results = await _repository.GetAllAsync(ct);
        results.ShouldNotContain(a => a.Id == agent.Id);
    }

    [Fact]
    public async Task GetByIdAsync_DeletedEntity_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "Will Be Deleted"
        };

        await _repository.CreateAsync(agent, ct);
        await _repository.DeleteAsync(agent.Id, ct);

        // Verify through GetAllAsync since FindAsync bypasses query filters.
        var results = await _repository.GetAllAsync(ct);
        results.ShouldNotContain(a => a.Id == agent.Id);
    }

    [Fact]
    public async Task UpdateAsync_ExistingEntity_PersistsChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            DisplayName = "Original Name"
        };

        await _repository.CreateAsync(agent, ct);

        agent.DisplayName = "Updated Name";
        await _repository.UpdateAsync(agent, ct);

        var result = await _repository.GetByIdAsync(agent.Id, ct);
        result.ShouldNotBeNull();
        result!.DisplayName.ShouldBe("Updated Name");
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
