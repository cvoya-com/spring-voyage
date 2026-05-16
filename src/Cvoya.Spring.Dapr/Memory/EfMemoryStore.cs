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
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogDebug(
            "Memory.Add owner={Owner} kind={Kind} id={Id}",
            owner, kind, entry.Id);

        return ToEntry(entry);
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
        return row is null ? null : ToEntry(row);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryEntry>> ListAsync(
        Address owner,
        MemoryKind? kind,
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

        var rows = await query
            .OrderByDescending(m => m.CreatedAt)
            .Skip(Math.Max(0, offset))
            .Take(Math.Max(1, limit))
            .ToListAsync(cancellationToken);

        return rows.Select(ToEntry).ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<MemoryEntry>> SearchAsync(
        Address owner,
        string query,
        MemoryKind? kind,
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

        return rows.Select(ToEntry).ToList();
    }

    /// <inheritdoc />
    public async Task<MemoryEntry?> UpdateAsync(
        Address owner,
        Guid id,
        string? content,
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

        if (mutated)
        {
            row.UpdatedAt = _timeProvider.GetUtcNow();
            await db.SaveChangesAsync(cancellationToken);
        }

        var refreshed = await db.Memories
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == row.Id, cancellationToken);
        return refreshed is null ? null : ToEntry(refreshed);
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

        db.Memories.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    private static MemoryEntry ToEntry(MemoryEntity row) =>
        new(
            Id: row.Id,
            Owner: new Address(row.OwnerScheme, row.OwnerId),
            Kind: (MemoryKind)row.Kind,
            Content: row.Content,
            Source: row.Source,
            ThreadId: row.ThreadId,
            CreatedAt: row.CreatedAt,
            UpdatedAt: row.UpdatedAt);
}
