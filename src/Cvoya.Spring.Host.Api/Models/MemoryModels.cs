// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using System.Text.Json;

/// <summary>
/// Response body for <c>GET /api/v1/tenant/units/{id}/memories</c> and
/// <c>GET /api/v1/tenant/agents/{id}/memories</c>. Partitions entries by
/// recall scope (#2997, ADR-0065): <c>agent</c> (durable knowledge
/// recalled across all conversations) and <c>thread</c> (private notes
/// bound to a single conversation / participant set).
/// </summary>
/// <remarks>
/// <para>
/// The endpoint is backed by the <c>IMemoryStore</c> (#2342) and
/// returns entries owned by the addressed agent or unit. The route
/// also accepts <c>scope</c> (<c>agent</c> / <c>thread</c>),
/// <c>limit</c>, <c>offset</c>, and <c>query</c> query-string
/// parameters; when <c>scope</c> is supplied the other side of the
/// response surfaces as an empty array so the wire shape stays stable
/// regardless of the filter. This operator inspector path lists every
/// thread's entries — the per-thread recall filter applies only to the
/// agent-runtime <c>sv.memory.*</c> tools.
/// </para>
/// </remarks>
/// <param name="Agent">Agent-scoped memory entries (recalled across all conversations).</param>
/// <param name="Thread">Thread-scoped memory entries (private notes bound to one conversation).</param>
public record MemoriesResponse(
    IReadOnlyList<MemoryEntry> Agent,
    IReadOnlyList<MemoryEntry> Thread);

/// <summary>
/// One memory entry. Wire form for both the read API and the operator
/// tab. Mirrors <c>Cvoya.Spring.Core.Memory.MemoryEntry</c>; the wire
/// uses string ids (32-char no-dash hex) so OpenAPI generators can
/// emit ergonomic client types.
/// </summary>
/// <param name="Id">Stable identifier for the memory entry.</param>
/// <param name="Content">
/// Entry content as a JSON value — a JSON string for a plain text note,
/// or an object/array for structured state. Surfaced in the inspector
/// with its JSON type preserved (mirrors <c>Core.Memory.MemoryEntry.Content</c>).
/// </param>
/// <param name="CreatedAt">UTC timestamp the entry was first captured.</param>
/// <param name="Source">
/// Optional origin of the entry (e.g. conversation id, message id).
/// Omitted when the entry has no referenceable upstream.
/// </param>
/// <param name="Scope">
/// Recall scope, derived from the thread binding. Values:
/// <c>"agent"</c> or <c>"thread"</c>.
/// </param>
/// <param name="UpdatedAt">UTC timestamp of the last mutation.</param>
/// <param name="ThreadId">
/// Thread the entry is bound to. Populated for thread-scoped entries;
/// null for agent-scoped entries.
/// </param>
public record MemoryEntry(
    string Id,
    JsonElement Content,
    DateTimeOffset CreatedAt,
    string? Source,
    string Scope,
    DateTimeOffset UpdatedAt,
    string? ThreadId = null);
