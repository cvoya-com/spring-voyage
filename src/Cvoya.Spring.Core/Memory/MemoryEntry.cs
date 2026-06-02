// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Memory;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// One memory entry. Owner-scoped to a single addressable (agent or
/// unit) per ADR-0036; the storage layer derives the (tenant,
/// owner_scheme, owner_id) triple from <see cref="Owner"/>.
/// </summary>
/// <remarks>
/// <para>
/// The <see cref="Scope"/> axis (#2997, ADR-0065) is <b>derived from</b>
/// <see cref="ThreadId"/> rather than stored: a null thread binding is
/// agent-scoped, a non-null binding is thread-scoped. The two therefore
/// can never disagree.
/// </para>
/// <para>
/// Topics were intentionally dropped from this record (#2342 follow-up).
/// They will return later as a *type* of memory once the memory model
/// evolves to a graph — collapsing the topic / entry split into a single
/// node-and-edge shape rather than two parallel tables.
/// </para>
/// </remarks>
/// <param name="Id">Stable Guid identifier for the entry.</param>
/// <param name="Owner">
/// Address of the owning agent or unit. Agent-scoped entries are scoped
/// on this alone; thread-scoped entries are scoped on
/// <c>(Owner, ThreadId)</c>.
/// </param>
/// <param name="Content">
/// The entry content, as a JSON value. A plain text note is a JSON
/// string (<see cref="JsonValueKind.String"/>); structured state is a
/// JSON object/array. The JSON kind is preserved end to end — the
/// storage layer persists this as a Postgres <c>jsonb</c> column — so
/// callers never stringify-and-reparse by hand.
/// </param>
/// <param name="Source">
/// Optional origin of the entry (e.g. message id, conversation id,
/// document reference). Omitted when the entry has no referenceable
/// upstream.
/// </param>
/// <param name="ThreadId">
/// The thread the entry is bound to. Non-null for thread-scoped entries
/// (see <see cref="Scope"/>); null for agent-scoped entries.
/// </param>
/// <param name="CreatedAt">UTC timestamp the entry was first captured.</param>
/// <param name="UpdatedAt">
/// UTC timestamp of the last content mutation; equal to
/// <see cref="CreatedAt"/> for entries that have never been updated.
/// </param>
public record MemoryEntry(
    Guid Id,
    Address Owner,
    JsonElement Content,
    string? Source,
    Guid? ThreadId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    /// <summary>
    /// Recall scope, derived from <see cref="ThreadId"/>:
    /// <see cref="MemoryScope.Agent"/> when there is no thread binding,
    /// <see cref="MemoryScope.Thread"/> otherwise. Never persisted —
    /// always computed from the thread binding (#2997).
    /// </summary>
    public MemoryScope Scope => ThreadId is null ? MemoryScope.Agent : MemoryScope.Thread;
}
