// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Memory;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Persistence abstraction for <see cref="MemoryEntry"/> rows owned by
/// agents and units (#2342). Every operation is owner-scoped — the
/// implementation derives the
/// <c>(tenant, owner_scheme, owner_id)</c> triple from the supplied
/// <see cref="Address"/> per ADR-0036 — and tenant-scoped via the
/// usual <c>SpringDbContext</c> query filter on the EF-backed
/// implementation.
/// </summary>
/// <remarks>
/// <para>
/// The store is the canonical entry point for the
/// <c>sv.memory_*</c> platform tools (#2342). It is dependency-free in
/// <c>Cvoya.Spring.Core</c> so cloud overlays can supply alternative
/// implementations (vector recall, encrypted-at-rest, …) without
/// taking a dependency on EF or any concrete storage layer.
/// </para>
/// </remarks>
public interface IMemoryStore
{
    /// <summary>
    /// Captures a new memory entry for <paramref name="owner"/>.
    /// </summary>
    /// <param name="owner">Owning agent or unit address.</param>
    /// <param name="kind">Long-term or short-term kind.</param>
    /// <param name="content">Raw entry text.</param>
    /// <param name="source">
    /// Optional origin of the entry (e.g. message id). May be
    /// <c>null</c> when the entry has no upstream reference.
    /// </param>
    /// <param name="threadId">
    /// Required for <see cref="MemoryKind.ShortTerm"/>; ignored (and
    /// persisted as <c>null</c>) for <see cref="MemoryKind.LongTerm"/>.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted entry, with server-assigned id + timestamps.</returns>
    Task<MemoryEntry> AddAsync(
        Address owner,
        MemoryKind kind,
        string content,
        string? source,
        Guid? threadId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the entry identified by <paramref name="id"/> if it
    /// exists and is owned by <paramref name="owner"/>; otherwise
    /// <c>null</c>.
    /// </summary>
    Task<MemoryEntry?> GetAsync(
        Address owner,
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists entries owned by <paramref name="owner"/>, optionally
    /// filtered by <paramref name="kind"/>. Ordered by <c>CreatedAt</c>
    /// desc so the most recent entries surface first.
    /// </summary>
    Task<IReadOnlyList<MemoryEntry>> ListAsync(
        Address owner,
        MemoryKind? kind,
        int limit,
        int offset,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Free-text searches entries owned by <paramref name="owner"/>.
    /// Results are ordered by relevance (highest first) using the
    /// underlying storage layer's full-text ranking; the EF / Postgres
    /// implementation uses <c>ts_rank</c> against a GIN-indexed
    /// <c>to_tsvector('english', content)</c> column.
    /// </summary>
    /// <param name="owner">Owning agent or unit.</param>
    /// <param name="query">Free-text search query.</param>
    /// <param name="kind">Optional kind filter.</param>
    /// <param name="limit">Maximum hits to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<MemoryEntry>> SearchAsync(
        Address owner,
        string query,
        MemoryKind? kind,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mutates the content of an existing entry. Pass <c>null</c> for
    /// the content to leave it untouched (partial-update semantics).
    /// Returns the updated entry, or <c>null</c> when the entry does
    /// not exist or is owned by a different addressable.
    /// </summary>
    Task<MemoryEntry?> UpdateAsync(
        Address owner,
        Guid id,
        string? content,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the entry identified by <paramref name="id"/> if it is
    /// owned by <paramref name="owner"/>. Returns <c>true</c> when a
    /// row was deleted; <c>false</c> for unknown or mis-owned ids.
    /// </summary>
    Task<bool> DeleteAsync(
        Address owner,
        Guid id,
        CancellationToken cancellationToken = default);
}
