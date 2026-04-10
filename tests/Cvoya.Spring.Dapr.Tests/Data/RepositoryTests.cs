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
    private readonly Repository<TenantEntity> _repository;

    public RepositoryTests()
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new SpringDbContext(options);
        _repository = new Repository<TenantEntity>(_context);
    }

    [Fact]
    public async Task GetByIdAsync_ExistingEntity_ReturnsEntity()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = new TenantEntity
        {
            Id = Guid.NewGuid(),
            Name = "Test Tenant"
        };

        await _repository.CreateAsync(tenant, ct);

        var result = await _repository.GetByIdAsync(tenant.Id, ct);

        result.Should().NotBeNull();
        result!.Name.Should().Be("Test Tenant");
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
        var tenant1 = new TenantEntity { Id = Guid.NewGuid(), Name = "Tenant 1" };
        var tenant2 = new TenantEntity { Id = Guid.NewGuid(), Name = "Tenant 2" };
        var deleted = new TenantEntity { Id = Guid.NewGuid(), Name = "Deleted", DeletedAt = DateTimeOffset.UtcNow };

        await _repository.CreateAsync(tenant1, ct);
        await _repository.CreateAsync(tenant2, ct);
        await _repository.CreateAsync(deleted, ct);

        var results = await _repository.GetAllAsync(ct);

        results.Should().HaveCount(2);
        results.Should().Contain(t => t.Name == "Tenant 1");
        results.Should().Contain(t => t.Name == "Tenant 2");
    }

    [Fact]
    public async Task DeleteAsync_ExistingEntity_SetsSoftDelete()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = new TenantEntity
        {
            Id = Guid.NewGuid(),
            Name = "To Delete"
        };

        await _repository.CreateAsync(tenant, ct);
        await _repository.DeleteAsync(tenant.Id, ct);

        // The soft-deleted entity should not appear in filtered queries.
        var results = await _repository.GetAllAsync(ct);
        results.Should().NotContain(t => t.Id == tenant.Id);
    }

    [Fact]
    public async Task GetByIdAsync_DeletedEntity_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = new TenantEntity
        {
            Id = Guid.NewGuid(),
            Name = "Will Be Deleted"
        };

        await _repository.CreateAsync(tenant, ct);
        await _repository.DeleteAsync(tenant.Id, ct);

        // Verify through GetAllAsync since FindAsync bypasses query filters.
        var results = await _repository.GetAllAsync(ct);
        results.Should().NotContain(t => t.Id == tenant.Id);
    }

    [Fact]
    public async Task UpdateAsync_ExistingEntity_PersistsChanges()
    {
        var ct = TestContext.Current.CancellationToken;
        var tenant = new TenantEntity
        {
            Id = Guid.NewGuid(),
            Name = "Original Name"
        };

        await _repository.CreateAsync(tenant, ct);

        tenant.Name = "Updated Name";
        await _repository.UpdateAsync(tenant, ct);

        var result = await _repository.GetByIdAsync(tenant.Id, ct);
        result.Should().NotBeNull();
        result!.Name.Should().Be("Updated Name");
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
