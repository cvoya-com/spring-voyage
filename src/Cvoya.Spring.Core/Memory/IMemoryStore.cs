// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Memory;

using System.Text.Json;

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
/// <para>
/// <b>Scope is derived, not stored</b> (#2997, ADR-0065). An entry's
/// <see cref="MemoryScope"/> follows directly from its <c>thread_id</c>:
/// no thread binding is <see cref="MemoryScope.Agent"/>-scoped, a
/// thread binding is <see cref="MemoryScope.Thread"/>-scoped.
/// </para>
/// </remarks>
public interface IMemoryStore
{
    /// <summary>
    /// Captures a new memory entry for <paramref name="owner"/>. The
    /// entry's scope is implied by <paramref name="threadId"/>: a null
    /// thread binding stores an agent-scoped entry, a non-null binding
    /// stores a thread-scoped one (#2997).
    /// </summary>
    /// <param name="owner">Owning agent or unit address.</param>
    /// <param name="content">
    /// The entry content, as a JSON value. A plain text note is a JSON
    /// string; structured state is a JSON object/array. The value is
    /// persisted to a <c>jsonb</c> column and its JSON kind is preserved
    /// on read. Must not be <see cref="JsonValueKind.Undefined"/> or
    /// <see cref="JsonValueKind.Null"/>.
    /// </param>
    /// <param name="source">
    /// Optional origin of the entry (e.g. message id). May be
    /// <c>null</c> when the entry has no upstream reference.
    /// </param>
    /// <param name="threadId">
    /// The thread to bind the entry to. <c>null</c> stores an
    /// agent-scoped entry; a value stores a thread-scoped entry.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The persisted entry, with server-assigned id + timestamps.</returns>
    Task<MemoryEntry> AddAsync(
        Address owner,
        JsonElement content,
        string? source,
        Guid? threadId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the entry identified by <paramref name="id"/> if it
    /// exists and is owned by <paramref name="owner"/>; otherwise
    /// <c>null</c>. A direct id fetch is owner-scoped only — it is not
    /// subject to the thread recall filter applied by
    /// <see cref="ListAsync"/> / <see cref="SearchAsync"/>.
    /// </summary>
    Task<MemoryEntry?> GetAsync(
        Address owner,
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists entries owned by <paramref name="owner"/>, optionally
    /// filtered by <paramref name="scope"/> and restricted by the thread
    /// recall filter. Ordered by <c>CreatedAt</c> desc so the most recent
    /// entries surface first.
    /// </summary>
    /// <param name="owner">Owning agent or unit.</param>
    /// <param name="scope">
    /// Optional scope filter. <see cref="MemoryScope.Agent"/> selects
    /// entries with no thread binding; <see cref="MemoryScope.Thread"/>
    /// selects thread-bound entries; <c>null</c> selects both.
    /// </param>
    /// <param name="recallThreadId">
    /// Recall filter (#2997, ADR-0065). When non-null, thread-scoped
    /// entries are restricted to this thread (agent-scoped entries are
    /// always included) — i.e. an agent operating inside a thread recalls
    /// only that thread's private notes. When <c>null</c>, no thread
    /// restriction is applied (the operator inspector path, which sees
    /// every thread's entries).
    /// </param>
    /// <param name="limit">Maximum entries to return.</param>
    /// <param name="offset">Offset into the result set for paging.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<MemoryEntry>> ListAsync(
        Address owner,
        MemoryScope? scope,
        Guid? recallThreadId,
        int limit,
        int offset,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Free-text searches entries owned by <paramref name="owner"/>.
    /// Results are ordered by relevance (highest first) using the
    /// underlying storage layer's full-text ranking; the EF / Postgres
    /// implementation uses <c>ts_rank</c> against a GIN-indexed
    /// <c>to_tsvector('english', content)</c> expression. Because
    /// <c>content</c> is <c>jsonb</c>, <c>to_tsvector</c> extracts the
    /// string values from the document (top-level text for a string
    /// entry; the string-typed values for a structured entry — object
    /// keys are not indexed), so structured memories are searchable by
    /// the text they contain.
    /// </summary>
    /// <param name="owner">Owning agent or unit.</param>
    /// <param name="query">Free-text search query.</param>
    /// <param name="scope">Optional scope filter (see <see cref="ListAsync"/>).</param>
    /// <param name="recallThreadId">
    /// Thread recall filter (see <see cref="ListAsync"/>): restricts
    /// thread-scoped hits to this thread when non-null; unrestricted when
    /// <c>null</c>.
    /// </param>
    /// <param name="limit">Maximum hits to return.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task<IReadOnlyList<MemoryEntry>> SearchAsync(
        Address owner,
        string query,
        MemoryScope? scope,
        Guid? recallThreadId,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mutates the content of an existing entry. Pass <c>null</c> for
    /// the content to leave it untouched (partial-update semantics); a
    /// non-null <paramref name="content"/> replaces the value, and its
    /// JSON kind may differ from the original (text ↔ structured). The
    /// <see cref="JsonElement"/> must not be
    /// <see cref="JsonValueKind.Undefined"/> or
    /// <see cref="JsonValueKind.Null"/>. Returns the updated entry, or
    /// <c>null</c> when the entry does not exist or is owned by a
    /// different addressable.
    /// </summary>
    Task<MemoryEntry?> UpdateAsync(
        Address owner,
        Guid id,
        JsonElement? content,
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
