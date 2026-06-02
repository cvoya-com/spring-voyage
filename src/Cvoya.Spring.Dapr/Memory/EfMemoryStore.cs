// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Memory;

using System.Text.Json;

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
/// <b>Content shape.</b> <c>content</c> is a <c>jsonb</c> column holding
/// the entry as a JSON value (a JSON string for a plain text note; an
/// object/array for structured state). <see cref="MemoryEntry.Content"/>
/// is a <see cref="JsonElement"/>; this store serialises it in and parses
/// it back out, so the JSON kind round-trips without callers
/// stringifying by hand.
/// </para>
/// <para>
/// <b>Full-text search.</b> The store prefers Postgres FTS — keys the
/// query off <c>EF.Functions.ToTsVector("english", content).Matches(
/// EF.Functions.PlainToTsQuery("english", query))</c>, ordered by
/// <c>ts_rank</c> desc. Because <c>content</c> is <c>jsonb</c>, the
/// <c>to_tsvector(jsonb)</c> overload extracts the document's string
/// values (top-level text for a string entry; string-typed values for a
/// structured one), so structured memories are searchable by the text
/// they contain. The store falls back to case-insensitive substring
/// matching when the active provider is not Postgres (notably the EF
/// in-memory provider used by the unit + integration tests); the
/// substring fallback is an approximation of the Postgres relevance
/// path, not a parity guarantee.
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
        JsonElement content,
        string? source,
        Guid? threadId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        EnsureStorableContent(content);

        await using var dbScope = _scopeFactory.CreateAsyncScope();
        var db = dbScope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var now = _timeProvider.GetUtcNow();
        var entry = new MemoryEntity
        {
            Id = Guid.NewGuid(),
            OwnerScheme = owner.Scheme,
            OwnerId = owner.Id,
            // Scope is derived from the thread binding (#2997): a null
            // thread id is agent-scoped, a value is thread-scoped.
            ThreadId = threadId,
            Content = Serialize(content),
            Source = source,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Memories.Add(entry);
        await db.SaveChangesAsync(cancellationToken);

        _logger.LogDebug(
            "Memory.Add owner={Owner} scope={Scope} id={Id}",
            owner, threadId is null ? "agent" : "thread", entry.Id);

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
        MemoryScope? scope,
        Guid? recallThreadId,
        int limit,
        int offset,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);

        await using var dbScope = _scopeFactory.CreateAsyncScope();
        var db = dbScope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var query = db.Memories
            .AsNoTracking()
            .Where(m => m.OwnerScheme == owner.Scheme && m.OwnerId == owner.Id);

        query = ApplyScopeFilters(query, scope, recallThreadId);

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
        MemoryScope? scope,
        Guid? recallThreadId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (string.IsNullOrWhiteSpace(query))
        {
            return Array.Empty<MemoryEntry>();
        }

        await using var dbScope = _scopeFactory.CreateAsyncScope();
        var db = dbScope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var baseQuery = db.Memories
            .AsNoTracking()
            .Where(m => m.OwnerScheme == owner.Scheme && m.OwnerId == owner.Id);

        baseQuery = ApplyScopeFilters(baseQuery, scope, recallThreadId);

        List<MemoryEntity> rows;
        var providerName = db.Database.ProviderName ?? string.Empty;
        if (providerName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            // Postgres-native full-text path: GIN(to_tsvector('english',
            // content)) index makes the Matches() predicate cheap and
            // ts_rank gives a deterministic relevance ordering. content
            // is jsonb, so to_tsvector uses the jsonb overload and
            // tokenises the document's string values.
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
            // Provider fallback (EF in-memory used in unit + integration
            // tests): case-insensitive substring match on the raw JSON
            // text, ordered by recency. Approximates the Postgres
            // relevance path (which is the production code path).
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
        JsonElement? content,
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
        if (content is { } newContent)
        {
            EnsureStorableContent(newContent);
            var serialized = Serialize(newContent);
            if (!string.Equals(serialized, row.Content, StringComparison.Ordinal))
            {
                row.Content = serialized;
                mutated = true;
            }
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
            Content: Parse(row.Content),
            Source: row.Source,
            ThreadId: row.ThreadId,
            CreatedAt: row.CreatedAt,
            UpdatedAt: row.UpdatedAt);

    /// <summary>
    /// Serialises a content <see cref="JsonElement"/> to its raw JSON
    /// text for the <c>jsonb</c> column (Postgres re-canonicalises on
    /// write; the in-memory provider stores the text verbatim).
    /// </summary>
    private static string Serialize(JsonElement content) =>
        JsonSerializer.Serialize(content);

    /// <summary>
    /// Parses the stored raw JSON back into a detached
    /// <see cref="JsonElement"/> (cloned so it outlives the parsing
    /// document).
    /// </summary>
    private static JsonElement Parse(string rawJson)
    {
        using var doc = JsonDocument.Parse(rawJson);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Rejects content that carries no memory: a missing
    /// (<see cref="JsonValueKind.Undefined"/>) or explicit JSON
    /// <c>null</c> value.
    /// </summary>
    private static void EnsureStorableContent(JsonElement content)
    {
        if (content.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            throw new ArgumentException(
                "Memory content must be a non-null JSON value.", nameof(content));
        }
    }

    /// <summary>
    /// Applies the derived scope filter and the thread recall filter
    /// (#2997). Scope is derived from <c>thread_id</c>:
    /// <see cref="MemoryScope.Agent"/> ⇒ <c>thread_id IS NULL</c>,
    /// <see cref="MemoryScope.Thread"/> ⇒ <c>thread_id IS NOT NULL</c>.
    /// The recall filter, when a thread is supplied, restricts
    /// thread-scoped rows to that thread (agent-scoped rows always pass)
    /// so an agent recalls only the current thread's private notes; a
    /// null recall thread applies no restriction (the operator inspector
    /// path, which sees every thread's entries).
    /// </summary>
    private static IQueryable<MemoryEntity> ApplyScopeFilters(
        IQueryable<MemoryEntity> query,
        MemoryScope? scope,
        Guid? recallThreadId)
    {
        if (scope == MemoryScope.Agent)
        {
            query = query.Where(m => m.ThreadId == null);
        }
        else if (scope == MemoryScope.Thread)
        {
            query = query.Where(m => m.ThreadId != null);
        }

        if (recallThreadId is { } tid)
        {
            query = query.Where(m => m.ThreadId == null || m.ThreadId == tid);
        }

        return query;
    }
}
