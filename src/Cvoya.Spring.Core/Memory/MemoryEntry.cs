// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Memory;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// One memory entry. Owner-scoped to a single addressable (agent or
/// unit) per ADR-0036; the storage layer derives the (tenant,
/// owner_scheme, owner_id) triple from <see cref="Owner"/>.
/// </summary>
/// <param name="Id">Stable Guid identifier for the entry.</param>
/// <param name="Owner">
/// Address of the owning agent or unit. Long-term entries are scoped on
/// this alone; short-term entries are scoped on
/// <c>(Owner, ThreadId)</c>.
/// </param>
/// <param name="Kind">
/// Discriminator between owner-scoped <see cref="MemoryKind.LongTerm"/>
/// and owner+thread-scoped <see cref="MemoryKind.ShortTerm"/>.
/// </param>
/// <param name="Content">Raw entry text.</param>
/// <param name="Source">
/// Optional origin of the entry (e.g. message id, conversation id,
/// document reference). Omitted when the entry has no referenceable
/// upstream.
/// </param>
/// <param name="ThreadId">
/// The thread the entry was captured in. Required for
/// <see cref="MemoryKind.ShortTerm"/>; null for
/// <see cref="MemoryKind.LongTerm"/>.
/// </param>
/// <param name="CreatedAt">UTC timestamp the entry was first captured.</param>
/// <param name="UpdatedAt">
/// UTC timestamp of the last content / topic mutation; equal to
/// <see cref="CreatedAt"/> for entries that have never been updated.
/// </param>
/// <param name="TopicIds">
/// Topics this entry is associated with. The list may be empty; entries
/// without topics surface in <c>SearchAsync</c> via free-text only.
/// </param>
public record MemoryEntry(
    Guid Id,
    Address Owner,
    MemoryKind Kind,
    string Content,
    string? Source,
    Guid? ThreadId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<Guid> TopicIds);
