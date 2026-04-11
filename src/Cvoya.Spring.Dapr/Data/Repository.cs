// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Generic EF Core repository implementation with soft-delete support.
/// Entities with a DeletedAt property are soft-deleted rather than removed from the database.
/// </summary>
/// <typeparam name="T">The entity type.</typeparam>
public class Repository<T>(SpringDbContext context) : IRepository<T> where T : class
{
    private readonly DbSet<T> _dbSet = context.Set<T>();

    /// <inheritdoc />
    public async Task<T?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _dbSet.FindAsync([id], cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<T>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dbSet.ToListAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<T> CreateAsync(T entity, CancellationToken cancellationToken = default)
    {
        _dbSet.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        return entity;
    }

    /// <inheritdoc />
    public async Task UpdateAsync(T entity, CancellationToken cancellationToken = default)
    {
        _dbSet.Update(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var entity = await _dbSet.FindAsync([id], cancellationToken);
        if (entity is null)
        {
            return;
        }

        // Attempt soft delete by setting DeletedAt property.
        var deletedAtProperty = context.Entry(entity).Properties
            .FirstOrDefault(p => p.Metadata.Name == "DeletedAt");

        if (deletedAtProperty is not null)
        {
            deletedAtProperty.CurrentValue = DateTimeOffset.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
        }
        else
        {
            // Fall back to hard delete for entities without DeletedAt.
            _dbSet.Remove(entity);
            await context.SaveChangesAsync(cancellationToken);
        }
    }
}