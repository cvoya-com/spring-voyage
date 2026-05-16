// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Memory;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Memory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// EF Core / Postgres implementation of <see cref="IMemoryTopicStore"/>
/// (#2342). Same singleton + per-call scope pattern as
/// <see cref="EfMemoryStore"/>.
/// </summary>
public sealed class EfMemoryTopicStore : IMemoryTopicStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EfMemoryTopicStore> _logger;

    /// <summary>Builds the store with its narrow singleton dependencies.</summary>
    public EfMemoryTopicStore(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<EfMemoryTopicStore> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MemoryTopic> AddAsync(
        Address owner,
        string name,
        string? description,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var existing = await db.MemoryTopics
            .AsNoTracking()
            .Where(t => t.OwnerScheme == owner.Scheme
                && t.OwnerId == owner.Id
                && t.Name == name)
            .FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            throw new SpringException(
                $"Topic '{name}' already exists for owner {owner}.");
        }

        var now = _timeProvider.GetUtcNow();
        var row = new MemoryTopicEntity
        {
            Id = Guid.NewGuid(),
            OwnerScheme = owner.Scheme,
            OwnerId = owner.Id,
            Name = name,
            Description = description,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.MemoryTopics.Add(row);
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogDebug("MemoryTopic.Add owner={Owner} name={Name} id={Id}",
            owner, name, row.Id);

        return ToTopic(row);
    }

    /// <inheritdoc />
    public async Task<MemoryTopic?> GetAsync(
        Address owner,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var row = await db.MemoryTopics
            .AsNoTracking()
            .Where(t => t.Id == id
                && t.OwnerScheme == owner.Scheme
                && t.OwnerId == owner.Id)
            .FirstOrDefaultAsync(cancellationToken);
        return row is null ? null : ToTopic(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryTopic>> ListAsync(
        Address owner,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var rows = await db.MemoryTopics
            .AsNoTracking()
            .Where(t => t.OwnerScheme == owner.Scheme && t.OwnerId == owner.Id)
            .OrderBy(t => t.Name)
            .Skip(Math.Max(0, offset))
            .Take(Math.Max(1, limit))
            .ToListAsync(cancellationToken);
        return rows.Select(ToTopic).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryTopic>> SearchAsync(
        Address owner,
        string query,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<MemoryTopic>();
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        // Case-insensitive substring search over name + description.
        // Topics carry tighter constraints than memory entries — they
        // are small in number and short in text — so a deterministic
        // substring search keeps the LLM-facing surface predictable
        // without needing a separate FTS index.
        var needle = query.Trim().ToLowerInvariant();
        var rows = await db.MemoryTopics
            .AsNoTracking()
            .Where(t => t.OwnerScheme == owner.Scheme && t.OwnerId == owner.Id)
            .Where(t => t.Name.ToLower().Contains(needle)
                || (t.Description != null && t.Description.ToLower().Contains(needle)))
            .OrderBy(t => t.Name)
            .Take(Math.Max(1, limit))
            .ToListAsync(cancellationToken);
        return rows.Select(ToTopic).ToList();
    }

    /// <inheritdoc />
    public async Task<MemoryTopic?> UpdateAsync(
        Address owner,
        Guid id,
        string? name,
        string? description,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var row = await db.MemoryTopics
            .Where(t => t.Id == id
                && t.OwnerScheme == owner.Scheme
                && t.OwnerId == owner.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var mutated = false;
        if (name is not null && !string.Equals(name, row.Name, StringComparison.Ordinal))
        {
            // Owner-unique invariant — refuse a rename that would
            // collide. The unique index would catch this on
            // SaveChanges, but a pre-check yields a deterministic
            // SpringException instead of a DbUpdateException.
            var collision = await db.MemoryTopics
                .AsNoTracking()
                .Where(t => t.OwnerScheme == owner.Scheme
                    && t.OwnerId == owner.Id
                    && t.Id != row.Id
                    && t.Name == name)
                .AnyAsync(cancellationToken);
            if (collision)
            {
                throw new SpringException(
                    $"Topic '{name}' already exists for owner {owner}.");
            }
            row.Name = name;
            mutated = true;
        }

        if (description is not null
            && !string.Equals(description, row.Description, StringComparison.Ordinal))
        {
            row.Description = description;
            mutated = true;
        }

        if (mutated)
        {
            row.UpdatedAt = _timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }

        return ToTopic(row);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAsync(
        Address owner,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var row = await db.MemoryTopics
            .Where(t => t.Id == id
                && t.OwnerScheme == owner.Scheme
                && t.OwnerId == owner.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return false;
        }

        // Cascade-remove the junction rows. The underlying memory
        // entries survive — losing topic association does not lose the
        // entry itself per the IMemoryTopicStore.DeleteAsync contract.
        var links = await db.MemoryTopicLinks
            .Where(l => l.TopicId == row.Id)
            .ToListAsync(cancellationToken);
        if (links.Count > 0)
        {
            db.MemoryTopicLinks.RemoveRange(links);
        }

        db.MemoryTopics.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static MemoryTopic ToTopic(MemoryTopicEntity row) =>
        new(
            Id: row.Id,
            Owner: new Address(row.OwnerScheme, row.OwnerId),
            Name: row.Name,
            Description: row.Description,
            CreatedAt: row.CreatedAt,
            UpdatedAt: row.UpdatedAt);
}
