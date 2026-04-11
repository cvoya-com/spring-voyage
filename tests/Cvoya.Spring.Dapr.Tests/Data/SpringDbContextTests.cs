// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Data;

using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using FluentAssertions;

using Microsoft.EntityFrameworkCore;

using Xunit;

/// <summary>
/// Tests for <see cref="SpringDbContext"/> verifying entity persistence, soft deletes, and audit columns.
/// </summary>
public class SpringDbContextTests : IDisposable
{
    private readonly SpringDbContext _context;

    public SpringDbContextTests()
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new SpringDbContext(options);
    }

    [Fact]
    public async Task SaveChangesAsync_NewAgentDefinition_CanCreateAndRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            AgentId = "ada",
            Name = "Ada",
            Role = "Software Engineer"
        };

        _context.AgentDefinitions.Add(agent);
        await _context.SaveChangesAsync(ct);

        var retrieved = await _context.AgentDefinitions.FindAsync([agent.Id], ct);

        retrieved.Should().NotBeNull();
        retrieved!.AgentId.Should().Be("ada");
        retrieved.Name.Should().Be("Ada");
    }

    [Fact]
    public async Task SaveChangesAsync_SoftDeletedEntity_FilteredOutByDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            AgentId = "deleted-agent",
            Name = "To Be Deleted",
            DeletedAt = DateTimeOffset.UtcNow
        };

        _context.AgentDefinitions.Add(agent);
        await _context.SaveChangesAsync(ct);

        var results = await _context.AgentDefinitions.ToListAsync(ct);

        results.Should().NotContain(a => a.Id == agent.Id);
    }

    [Fact]
    public async Task SaveChangesAsync_NewEntity_PopulatesAuditColumns()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = DateTimeOffset.UtcNow;

        var agent = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            AgentId = "audit-test",
            Name = "Audit Test"
        };

        _context.AgentDefinitions.Add(agent);
        await _context.SaveChangesAsync(ct);

        var after = DateTimeOffset.UtcNow;

        agent.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        agent.UpdatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public async Task SaveChangesAsync_ModifiedEntity_UpdatesUpdatedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var agent = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            AgentId = "update-test",
            Name = "Original"
        };

        _context.AgentDefinitions.Add(agent);
        await _context.SaveChangesAsync(ct);

        var originalUpdatedAt = agent.UpdatedAt;

        // Small delay to ensure timestamp differs.
        await Task.Delay(10, ct);

        agent.Name = "Modified";
        _context.AgentDefinitions.Update(agent);
        await _context.SaveChangesAsync(ct);

        agent.UpdatedAt.Should().BeAfter(originalUpdatedAt);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}