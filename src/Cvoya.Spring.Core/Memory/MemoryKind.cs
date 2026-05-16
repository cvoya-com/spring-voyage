// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Memory;

/// <summary>
/// Kind discriminator for a <see cref="MemoryEntry"/> — distinguishes
/// owner-scoped long-term recall from owner+thread-scoped short-term
/// working memory.
/// </summary>
/// <remarks>
/// <para>
/// <b>Long-term</b> memory is owner-scoped: every entry is bound to the
/// owning agent or unit address. Long-term entries survive across
/// conversations and are recalled by topic / free-text search.
/// </para>
/// <para>
/// <b>Short-term</b> memory is owner+thread-scoped: every entry carries
/// a non-null <c>thread_id</c> and is meaningful only inside the thread
/// that produced it. Pruning short-term entries when the thread ends is
/// out of scope for the storage layer (the schema only carries the
/// <c>thread_id</c> so a future thread-lifecycle hook can do the work).
/// </para>
/// </remarks>
public enum MemoryKind
{
    /// <summary>Owner-scoped long-term memory. Persists across threads.</summary>
    LongTerm = 0,

    /// <summary>Owner+thread-scoped short-term working memory.</summary>
    ShortTerm = 1,
}
