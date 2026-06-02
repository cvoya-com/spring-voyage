// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persists one memory entry owned by an agent or unit (#2342). The
/// <c>(tenant_id, owner_scheme, owner_id)</c> triple scopes every read /
/// write per ADR-0036. Long-term entries carry <c>thread_id == null</c>;
/// short-term entries carry a non-null <c>thread_id</c> so a future
/// thread-lifecycle hook can prune them when the thread ends.
/// </summary>
/// <remarks>
/// <para>
/// The <c>content</c> column is <c>jsonb</c>: it holds the entry as a
/// JSON value (a JSON string for a plain text note; an object/array for
/// structured state). The CLR property is the raw JSON text;
/// <c>EfMemoryStore</c> serialises a <c>JsonElement</c> in and parses
/// one back out.
/// </para>
/// <para>
/// Full-text search keys off the <c>content</c> column via a Postgres
/// <c>GIN(to_tsvector('english', content))</c> index — the
/// <c>to_tsvector(jsonb)</c> overload extracts the document's string
/// values. See the <c>EfMemoryStore.SearchAsync</c> implementation.
/// </para>
/// </remarks>
public class MemoryEntity : ITenantScopedEntity
{
    /// <summary>Stable Guid identifier for the entry.</summary>
    public Guid Id { get; set; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>
    /// Address scheme of the owning addressable — one of
    /// <c>"agent"</c> or <c>"unit"</c>.
    /// </summary>
    public string OwnerScheme { get; set; } = string.Empty;

    /// <summary>Stable Guid identity of the owning addressable.</summary>
    public Guid OwnerId { get; set; }

    /// <summary>
    /// Memory kind discriminator (int representation of
    /// <see cref="Cvoya.Spring.Core.Memory.MemoryKind"/>): 0 = LongTerm,
    /// 1 = ShortTerm. Stored as <c>int</c> so the column survives
    /// future enum additions without DDL changes.
    /// </summary>
    public int Kind { get; set; }

    /// <summary>
    /// Thread id for short-term entries; <c>null</c> for long-term
    /// entries.
    /// </summary>
    public Guid? ThreadId { get; set; }

    /// <summary>
    /// Raw JSON text of the entry content, persisted to a <c>jsonb</c>
    /// column. A plain text note is stored as a JSON string (e.g.
    /// <c>"remember this"</c>); structured state as an object/array.
    /// Defaults to the JSON literal <c>null</c> so the non-null column
    /// always has a valid jsonb value.
    /// </summary>
    public string Content { get; set; } = "null";

    /// <summary>
    /// Optional origin reference (message id, conversation id, document
    /// reference). <c>null</c> when the entry has no upstream.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>UTC timestamp when the entry was first captured.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC timestamp of the last content mutation.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
