// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Response body for <c>GET /api/v1/units/{id}/memories</c> and
/// <c>GET /api/v1/agents/{id}/memories</c>. Mirrors the two-axis shape
/// the design kit's Memory tab expects: short-term (in-flight conversational
/// context) and long-term (persisted recall across sessions).
/// <para>
/// v2.0 ships the endpoint <b>contract</b> only — both lists always return
/// empty. The write API + real backing store ship in <c>V21-memory-write</c>
/// per plan §13. The Memory tabs render an empty-state referencing v2.1
/// so reviewers can verify the wiring.
/// </para>
/// </summary>
/// <param name="ShortTerm">Short-term memory entries. Empty in v2.0.</param>
/// <param name="LongTerm">Long-term memory entries. Empty in v2.0.</param>
public record MemoriesResponse(
    IReadOnlyList<MemoryEntry> ShortTerm,
    IReadOnlyList<MemoryEntry> LongTerm);

/// <summary>
/// One memory entry. Shape is intentionally minimal-but-extensible — fields
/// added in the v2.1 write API should append to this record so existing
/// v2.0 clients continue to deserialize the short-term/long-term arrays
/// without touching the response.
/// </summary>
/// <param name="Id">Stable identifier for the memory entry.</param>
/// <param name="Content">Raw entry text surfaced in the inspector.</param>
/// <param name="CreatedAt">UTC timestamp the entry was captured.</param>
/// <param name="Source">
/// Optional origin of the entry (e.g. conversation id, message id). Omitted
/// when the entry has no referenceable upstream.
/// </param>
public record MemoryEntry(
    string Id,
    string Content,
    DateTimeOffset CreatedAt,
    string? Source = null);
