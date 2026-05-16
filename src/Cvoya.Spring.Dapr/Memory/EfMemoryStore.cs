// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Memory;

using Cvoya.Spring.Core.Memory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// EF Core / Postgres implementation of <see cref="IMemoryStore"/>
/// (#2342). Owner- and tenant-scoped on top of <c>SpringDbContext</c>'s
/// query filter; the singleton creates a fresh <c>IServiceScope</c> per
/// call so the scoped <see cref="SpringDbContext"/> resolves cleanly
/// from singleton call sites (same pattern as
/// <c>UnitConnectorBindingStore</c>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Full-text search.</b> The store prefers Postgres FTS — keys the
/// query off <c>EF.Functions.ToTsVector("english", content).Matches(
/// EF.Functions.PlainToTsQuery("english", query))</c>, ordered by
/// <c>ts_rank</c> desc — and falls back to case-insensitive substring
/// matching when the active provider is not Postgres (notably the EF
/// in-memory provider used by the fast unit tests). The fallback keeps
/// the unit tests provider-agnostic; production code path is exercised
/// by the integration suite on a real Postgres instance.
/// </para>
/// </remarks>
public sealed class EfMemoryStore : IMemoryStore
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<EfMemoryStore> _logger;

    /// <summary>Builds the store with its narrow singleton dependencies.</summary>
    public EfMemoryStore(
        IServiceScopeFactory scopeFactory,
        TimeProvider timeProvider,
        ILogger<EfMemoryStore> logger)
    {
        _scopeFactory = scopeFactory;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<MemoryEntry> AddAsync(
        Address owner,
        MemoryKind kind,
        string content,
        string? source,
        Guid? threadId,
        IReadOnlyList<Guid> topicIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        if (kind == MemoryKind.ShortTerm && !threadId.HasValue)
        {
            throw new ArgumentException("Short-term entries require a thread id.", nameof(threadId));
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var now = _timeProvider.GetUtcNow();
        var entry = new MemoryEntity
        {
            Id = Guid.NewGuid(),
            OwnerScheme = owner.Scheme,
            OwnerId = owner.Id,
            Kind = (int)kind,
            ThreadId = kind == MemoryKind.LongTerm ? null : threadId,
            Content = content,
            Source = source,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Memories.Add(entry);

        // Drop topic ids that aren't owned by the same addressable. The
        // store accepts mis-typed ids gracefully — a tool caller that
        // passes a topic id from a different owner should not see the
        // add fail; the link is dropped instead so partial wiring
        // self-heals.
        var ownedTopicIds = await ResolveOwnedTopicIdsAsync(db, owner, topicIds, cancellationToken);
        foreach (var topicId in ownedTopicIds)
        {
            db.MemoryTopicLinks.Add(new MemoryTopicLinkEntity
            {
                MemoryId = entry.Id,
                TopicId = topicId,
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        _logger.LogDebug(
            "Memory.Add owner={Owner} kind={Kind} topics={TopicCount} id={Id}",
            owner, kind, ownedTopicIds.Count, entry.Id);

        return ToEntry(entry, ownedTopicIds);
    }

    /// <inheritdoc />
    public async Task<MemoryEntry?> GetAsync(
        Address owner,
        Guid id,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var row = await db.Memories
            .AsNoTracking()
            .Where(m => m.Id == id
                && m.OwnerScheme == owner.Scheme
                && m.OwnerId == owner.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var topicIds = await ReadTopicIdsAsync(db, row.Id, cancellationToken);
        return ToEntry(row, topicIds);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryEntry>> ListAsync(
        Address owner,
        MemoryKind? kind,
        Guid? topicId,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var query = db.Memories
            .AsNoTracking()
            .Where(m => m.OwnerScheme == owner.Scheme && m.OwnerId == owner.Id);

        if (kind is { } k)
        {
            var kindInt = (int)k;
            query = query.Where(m => m.Kind == kindInt);
        }

        if (topicId is { } t)
        {
            // Join through the link table to filter by topic. The
            // tenant filter on MemoryTopicLinks fires automatically;
            // we still constrain on tenant + topic via the where below
            // to keep the plan simple.
            query = query.Where(m =>
                db.MemoryTopicLinks.Any(l => l.MemoryId == m.Id && l.TopicId == t));
        }

        var rows = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip(Math.Max(0, offset))
            .Take(Math.Max(1, limit))
            .ToListAsync(cancellationToken);

        return await HydrateEntriesAsync(db, rows, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryEntry>> SearchAsync(
        Address owner,
        string query,
        MemoryKind? kind,
        Guid? topicId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<MemoryEntry>();
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var baseQuery = db.Memories
            .AsNoTracking()
            .Where(m => m.OwnerScheme == owner.Scheme && m.OwnerId == owner.Id);

        if (kind is { } k)
        {
            var kindInt = (int)k;
            baseQuery = baseQuery.Where(m => m.Kind == kindInt);
        }

        if (topicId is { } t)
        {
            baseQuery = baseQuery.Where(m =>
                db.MemoryTopicLinks.Any(l => l.MemoryId == m.Id && l.TopicId == t));
        }

        List<MemoryEntity> rows;
        var providerName = db.Database.ProviderName ?? string.Empty;
        if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            // Postgres-native full-text path: GIN(to_tsvector('english',
            // content)) index makes the Matches() predicate cheap and
            // ts_rank gives a deterministic relevance ordering.
            rows = await baseQuery
                .Where(m => EF.Functions
                    .ToTsVector("english", m.Content)
                    .Matches(EF.Functions.PlainToTsQuery("english", query)))
                .OrderByDescending(m => EF.Functions
                    .ToTsVector("english", m.Content)
                    .Rank(EF.Functions.PlainToTsQuery("english", query)))
                .ThenByDescending(m => m.CreatedAt)
                .Take(Math.Max(1, limit))
                .ToListAsync(cancellationToken);
        }
        else
        {
            // Provider fallback (EF in-memory used in unit tests):
            // case-insensitive substring match on Content, ordered by
            // recency. The integration suite covers the real Postgres
            // FTS path.
            var needle = query.Trim();
            rows = await baseQuery
                .Where(m => m.Content.ToLower().Contains(needle.ToLower()))
                .OrderByDescending(m => m.CreatedAt)
                .Take(Math.Max(1, limit))
                .ToListAsync(cancellationToken);
        }

        return await HydrateEntriesAsync(db, rows, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<MemoryEntry?> UpdateAsync(
        Address owner,
        Guid id,
        string? content,
        IReadOnlyList<Guid>? topicIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var row = await db.Memories
            .Where(m => m.Id == id
                && m.OwnerScheme == owner.Scheme
                && m.OwnerId == owner.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }

        var mutated = false;
        if (content is not null && !string.Equals(content, row.Content, StringComparison.Ordinal))
        {
            row.Content = content;
            mutated = true;
        }

        if (topicIds is not null)
        {
            // Replace the link set wholesale. SaveChanges below stamps
            // UpdatedAt via the audit pipeline.
            var existingLinks = await db.MemoryTopicLinks
                .Where(l => l.MemoryId == row.Id)
                .ToListAsync(cancellationToken);
            db.MemoryTopicLinks.RemoveRange(existingLinks);

            var ownedTopicIds = await ResolveOwnedTopicIdsAsync(db, owner, topicIds, cancellationToken);
            foreach (var topicId in ownedTopicIds)
            {
                db.MemoryTopicLinks.Add(new MemoryTopicLinkEntity
                {
                    MemoryId = row.Id,
                    TopicId = topicId,
                });
            }
            mutated = true;
        }

        if (mutated)
        {
            row.UpdatedAt = _timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }

        var refreshed = await db.Memories
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == row.Id, cancellationToken);
        if (refreshed is null)
        {
            return null;
        }

        var finalTopicIds = await ReadTopicIdsAsync(db, refreshed.Id, cancellationToken);
        return ToEntry(refreshed, finalTopicIds);
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

        var row = await db.Memories
            .Where(m => m.Id == id
                && m.OwnerScheme == owner.Scheme
                && m.OwnerId == owner.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return false;
        }

        var links = await db.MemoryTopicLinks
            .Where(l => l.MemoryId == row.Id)
            .ToListAsync(cancellationToken);
        if (links.Count > 0)
        {
            db.MemoryTopicLinks.RemoveRange(links);
        }
        db.Memories.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static async Task<IReadOnlyList<Guid>> ResolveOwnedTopicIdsAsync(
        SpringDbContext db,
        Address owner,
        IReadOnlyList<Guid> topicIds,
        CancellationToken cancellationToken)
    {
        if (topicIds is null || topicIds.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        var distinct = topicIds.Distinct().ToList();
        var owned = await db.MemoryTopics
            .AsNoTracking()
            .Where(t => t.OwnerScheme == owner.Scheme
                && t.OwnerId == owner.Id
                && distinct.Contains(t.Id))
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);
        return owned;
    }

    private static async Task<IReadOnlyList<Guid>> ReadTopicIdsAsync(
        SpringDbContext db,
        Guid memoryId,
        CancellationToken cancellationToken)
    {
        return await db.MemoryTopicLinks
            .AsNoTracking()
            .Where(l => l.MemoryId == memoryId)
            .Select(l => l.TopicId)
            .OrderBy(t => t)
            .ToListAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<MemoryEntry>> HydrateEntriesAsync(
        SpringDbContext db,
        List<MemoryEntity> rows,
        CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return Array.Empty<MemoryEntry>();
        }

        var ids = rows.Select(r => r.Id).ToList();
        var links = await db.MemoryTopicLinks
            .AsNoTracking()
            .Where(l => ids.Contains(l.MemoryId))
            .Select(l => new { l.MemoryId, l.TopicId })
            .ToListAsync(cancellationToken);

        var byMemory = links
            .GroupBy(l => l.MemoryId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(x => x.TopicId).OrderBy(x => x).ToList());

        var results = new List<MemoryEntry>(rows.Count);
        foreach (var row in rows)
        {
            byMemory.TryGetValue(row.Id, out var topics);
            results.Add(ToEntry(row, topics ?? Array.Empty<Guid>()));
        }
        return results;
    }

    private static MemoryEntry ToEntry(MemoryEntity row, IReadOnlyList<Guid> topicIds) =>
        new(
            Id: row.Id,
            Owner: new Address(row.OwnerScheme, row.OwnerId),
            Kind: (MemoryKind)row.Kind,
            Content: row.Content,
            Source: row.Source,
            ThreadId: row.ThreadId,
            CreatedAt: row.CreatedAt,
            UpdatedAt: row.UpdatedAt,
            TopicIds: topicIds);
}
