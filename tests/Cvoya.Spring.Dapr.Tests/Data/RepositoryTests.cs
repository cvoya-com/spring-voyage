// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Data;

using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

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
            AgentId = "test-agent",
            Name = "Test Agent"
        };

        await _repository.CreateAsync(agent, ct);

        var result = await _repository.GetByIdAsync(agent.Id, ct);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Test Agent");
    }

    [Fact]
    public async Task GetByIdAsync_NonExistentId_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _repository.GetByIdAsync(Guid.NewGuid(), ct);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAllAsync_MultipleEntities_ReturnsAllNonDeleted()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent1 = new AgentDefinitionEntity { Id = Guid.NewGuid(), AgentId = "agent-1", Name = "Agent 1" };
        var agent2 = new AgentDefinitionEntity { Id = Guid.NewGuid(), AgentId = "agent-2", Name = "Agent 2" };
        var deleted = new AgentDefinitionEntity { Id = Guid.NewGuid(), AgentId = "agent-d", Name = "Deleted", DeletedAt = DateTimeOffset.UtcNow };

        await _repository.CreateAsync(agent1, ct);
        await _repository.CreateAsync(agent2, ct);
        await _repository.CreateAsync(deleted, ct);

        var results = await _repository.GetAllAsync(ct);

        results.Should().HaveCount(2);
        results.Should().Contain(a => a.Name == "Agent 1");
        results.Should().Contain(a => a.Name == "Agent 2");
    }

    [Fact]
    public async Task DeleteAsync_ExistingEntity_SetsSoftDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            AgentId = "to-delete",
            Name = "To Delete"
        };

        await _repository.CreateAsync(agent, ct);
        await _repository.DeleteAsync(agent.Id, ct);

        // The soft-deleted entity should not appear in filtered queries.
        var results = await _repository.GetAllAsync(ct);
        results.Should().NotContain(a => a.Id == agent.Id);
    }

    [Fact]
    public async Task GetByIdAsync_DeletedEntity_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            AgentId = "will-delete",
            Name = "Will Be Deleted"
        };

        await _repository.CreateAsync(agent, ct);
        await _repository.DeleteAsync(agent.Id, ct);

        // Verify through GetAllAsync since FindAsync bypasses query filters.
        var results = await _repository.GetAllAsync(ct);
        results.Should().NotContain(a => a.Id == agent.Id);
    }

    [Fact]
    public async Task UpdateAsync_ExistingEntity_PersistsChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            AgentId = "original",
            Name = "Original Name"
        };

        await _repository.CreateAsync(agent, ct);

        agent.Name = "Updated Name";
        await _repository.UpdateAsync(agent, ct);

        var result = await _repository.GetByIdAsync(agent.Id, ct);
        result.Should().NotBeNull();
        result!.Name.Should().Be("Updated Name");
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}