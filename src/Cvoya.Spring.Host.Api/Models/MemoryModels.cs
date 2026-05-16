// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Response body for <c>GET /api/v1/tenant/units/{id}/memories</c> and
/// <c>GET /api/v1/tenant/agents/{id}/memories</c>. Mirrors the two-axis
/// shape the design kit's Memory tab expects: short-term (in-flight
/// conversational context) and long-term (persisted recall across
/// sessions).
/// </summary>
/// <remarks>
/// <para>
/// The endpoint is backed by the <c>IMemoryStore</c> (#2342) and
/// returns entries owned by the addressed agent or unit. The route
/// also accepts <c>kind</c> (<c>long_term</c> / <c>short_term</c>),
/// <c>limit</c>, <c>offset</c>, and <c>query</c> query-string
/// parameters; when <c>kind</c> is supplied the other side of the
/// response surfaces as an empty array so the wire shape stays stable
/// regardless of the filter.
/// </para>
/// </remarks>
/// <param name="ShortTerm">Short-term memory entries (thread-scoped working notes).</param>
/// <param name="LongTerm">Long-term memory entries (cross-thread recall).</param>
public record MemoriesResponse(
    IReadOnlyList<MemoryEntry> ShortTerm,
    IReadOnlyList<MemoryEntry> LongTerm);

/// <summary>
/// One memory entry. Wire form for both the read API and the operator
/// tab. Mirrors <c>Cvoya.Spring.Core.Memory.MemoryEntry</c>; the wire
/// uses string ids (32-char no-dash hex) so OpenAPI generators can
/// emit ergonomic client types.
/// </summary>
/// <param name="Id">Stable identifier for the memory entry.</param>
/// <param name="Content">Raw entry text surfaced in the inspector.</param>
/// <param name="CreatedAt">UTC timestamp the entry was first captured.</param>
/// <param name="Source">
/// Optional origin of the entry (e.g. conversation id, message id).
/// Omitted when the entry has no referenceable upstream.
/// </param>
/// <param name="Kind">
/// Memory kind. Values: <c>"long_term"</c> or <c>"short_term"</c>.
/// </param>
/// <param name="UpdatedAt">UTC timestamp of the last mutation.</param>
/// <param name="ThreadId">
/// Thread the entry was captured in. Populated for short-term entries;
/// null for long-term entries.
/// </param>
public record MemoryEntry(
    string Id,
    string Content,
    DateTimeOffset CreatedAt,
    string? Source,
    string Kind,
    DateTimeOffset UpdatedAt,
    string? ThreadId = null);
