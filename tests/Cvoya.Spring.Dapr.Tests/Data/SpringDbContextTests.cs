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
    public async Task SaveChangesAsync_NewTenant_CanCreateAndRead()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = new TenantEntity
        {
            Id = Guid.NewGuid(),
            Name = "Test Tenant"
        };

        _context.Tenants.Add(tenant);
        await _context.SaveChangesAsync(ct);

        var retrieved = await _context.Tenants.FindAsync([tenant.Id], ct);

        retrieved.Should().NotBeNull();
        retrieved!.Name.Should().Be("Test Tenant");
    }

    [Fact]
    public async Task SaveChangesAsync_NewAgentDefinition_CanCreateWithTenantReference()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = new TenantEntity
        {
            Id = Guid.NewGuid(),
            Name = "Test Tenant"
        };

        var agent = new AgentDefinitionEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            AgentId = "ada",
            Name = "Ada",
            Role = "Software Engineer",
            Tenant = tenant
        };

        _context.Tenants.Add(tenant);
        _context.AgentDefinitions.Add(agent);
        await _context.SaveChangesAsync(ct);

        var retrieved = await _context.AgentDefinitions
            .Include(a => a.Tenant)
            .FirstOrDefaultAsync(a => a.Id == agent.Id, ct);

        retrieved.Should().NotBeNull();
        retrieved!.AgentId.Should().Be("ada");
        retrieved.Tenant.Should().NotBeNull();
        retrieved.Tenant!.Name.Should().Be("Test Tenant");
    }

    [Fact]
    public async Task SaveChangesAsync_SoftDeletedEntity_FilteredOutByDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = new TenantEntity
        {
            Id = Guid.NewGuid(),
            Name = "To Be Deleted",
            DeletedAt = DateTimeOffset.UtcNow
        };

        _context.Tenants.Add(tenant);
        await _context.SaveChangesAsync(ct);

        var results = await _context.Tenants.ToListAsync(ct);

        results.Should().NotContain(t => t.Id == tenant.Id);
    }

    [Fact]
    public async Task SaveChangesAsync_NewEntity_PopulatesAuditColumns()
    {
        var ct = TestContext.Current.CancellationToken;
        var before = DateTimeOffset.UtcNow;

        var tenant = new TenantEntity
        {
            Id = Guid.NewGuid(),
            Name = "Audit Test"
        };

        _context.Tenants.Add(tenant);
        await _context.SaveChangesAsync(ct);

        var after = DateTimeOffset.UtcNow;

        tenant.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        tenant.UpdatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
    }

    [Fact]
    public async Task SaveChangesAsync_ModifiedEntity_UpdatesUpdatedAt()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = new TenantEntity
        {
            Id = Guid.NewGuid(),
            Name = "Original"
        };

        _context.Tenants.Add(tenant);
        await _context.SaveChangesAsync(ct);

        var originalUpdatedAt = tenant.UpdatedAt;

        // Small delay to ensure timestamp differs.
        await Task.Delay(10, ct);

        tenant.Name = "Modified";
        _context.Tenants.Update(tenant);
        await _context.SaveChangesAsync(ct);

        tenant.UpdatedAt.Should().BeAfter(originalUpdatedAt);
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
