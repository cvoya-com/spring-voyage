// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Memory;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Persistence abstraction for <see cref="MemoryTopic"/> rows owned by
/// agents and units (#2342). Topics group memory entries so the LLM can
/// recall by topic name instead of by full-text search.
/// </summary>
/// <remarks>
/// <para>
/// Owner-scoped per ADR-0036: an agent and a unit may both own a topic
/// named <c>"design-decisions"</c> without conflict. Deleting a topic
/// cascade-removes the <c>memory ↔ topic</c> link rows but does NOT
/// delete the underlying memory entries — they remain reachable via
/// free-text search and direct id lookup.
/// </para>
/// </remarks>
public interface IMemoryTopicStore
{
    /// <summary>
    /// Creates a new topic. Throws when a topic with the same name
    /// already exists for <paramref name="owner"/>.
    /// </summary>
    Task<MemoryTopic> AddAsync(
        Address owner,
        string name,
        string? description,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the topic identified by <paramref name="id"/> if owned
    /// by <paramref name="owner"/>; otherwise <c>null</c>.
    /// </summary>
    Task<MemoryTopic?> GetAsync(
        Address owner,
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists topics owned by <paramref name="owner"/>, ordered by
    /// <c>Name</c> ascending. Pagination via <paramref name="limit"/>
    /// and <paramref name="offset"/>.
    /// </summary>
    Task<IReadOnlyList<MemoryTopic>> ListAsync(
        Address owner,
        int limit,
        int offset,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Free-text searches topic name and description fields. Results
    /// are ordered by relevance; the EF / Postgres implementation uses
    /// case-insensitive substring matching with name-match preference.
    /// </summary>
    Task<IReadOnlyList<MemoryTopic>> SearchAsync(
        Address owner,
        string query,
        int limit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Mutates the topic's name and/or description. Pass <c>null</c>
    /// for a field to leave it untouched. Returns the updated topic,
    /// or <c>null</c> when the topic does not exist or is owned by a
    /// different addressable. Throws when the new name collides with
    /// another topic owned by the same addressable.
    /// </summary>
    Task<MemoryTopic?> UpdateAsync(
        Address owner,
        Guid id,
        string? name,
        string? description,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes the topic identified by <paramref name="id"/> if it is
    /// owned by <paramref name="owner"/>. Cascade-removes
    /// <c>memory_topic_links</c> rows so the underlying memory entries
    /// continue to exist but lose their association with this topic.
    /// Returns <c>true</c> when a row was deleted.
    /// </summary>
    Task<bool> DeleteAsync(
        Address owner,
        Guid id,
        CancellationToken cancellationToken = default);
}
