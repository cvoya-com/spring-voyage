// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Memory;

/// <summary>
/// Scope discriminator for a <see cref="MemoryEntry"/> — the axis along
/// which an entry is recalled. This is a <i>scope</i>, not a lifetime:
/// both values are equally durable (#2997, ADR-0065).
/// </summary>
/// <remarks>
/// <para>
/// The scope is <b>derived from</b> <see cref="MemoryEntry.ThreadId"/>
/// rather than stored: an entry with no <c>thread_id</c> is
/// <see cref="Agent"/>-scoped; an entry bound to a <c>thread_id</c> is
/// <see cref="Thread"/>-scoped. There is no separate persisted column,
/// so the two can never drift. This enum is a logical filter / projection
/// type only — it is never written to the database.
/// </para>
/// <para>
/// <b>Agent-scoped</b> (the default) is durable knowledge that applies
/// across <i>all</i> the agent's conversations. <b>Thread-scoped</b> is a
/// private note that applies <i>only within one thread / participant
/// set</i> (e.g. "in this thread, refer to agent XYZ as 'Bob'"). A
/// thread-scoped entry is recalled only while operating inside its
/// thread; an agent-scoped entry is recalled everywhere.
/// </para>
/// </remarks>
public enum MemoryScope
{
    /// <summary>
    /// Owner-scoped memory that applies across all of the owner's
    /// threads. Carries no <c>thread_id</c>.
    /// </summary>
    Agent = 0,

    /// <summary>
    /// Memory private to a single thread / participant set. Carries the
    /// <c>thread_id</c> it is bound to; recalled only within that thread.
    /// </summary>
    Thread = 1,
}
