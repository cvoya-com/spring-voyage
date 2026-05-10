// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

/// <summary>
/// Per-thread channel inside an agent's mailbox (#2076 / ADR-0030 §3 §44).
/// Holds the FIFO-ordered queue of messages for a single thread plus a
/// <see cref="Dispatching"/> flag that signals whether a dispatch loop is
/// currently draining this channel — preventing a fresh inbound message on
/// the same thread from launching a parallel dispatcher while the channel
/// is mid-drain. Concurrent threads on the same agent each carry their own
/// channel and run independently (per-thread FIFO is the only ordering
/// invariant); the agent-wide single-active-thread slot from the
/// pre-ADR-0030 mailbox is gone.
/// </summary>
public class ThreadChannel
{
    /// <summary>
    /// Gets the unique identifier for this thread.
    /// </summary>
    public string ThreadId { get; init; } = string.Empty;

    /// <summary>
    /// Gets or sets the queue of messages awaiting processing in this thread.
    /// </summary>
    public List<Message> Messages { get; set; } = [];

    /// <summary>
    /// Gets or sets the timestamp when this thread was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Gets or sets a flag indicating whether a dispatch loop is currently
    /// draining this channel. The mailbox sets this to <c>true</c> when it
    /// launches a dispatcher for the head message and clears it when the
    /// drain loop exits with no remaining messages. New inbound messages
    /// on the same thread append to <see cref="Messages"/> while
    /// <see cref="Dispatching"/> is <c>true</c> and rely on the active
    /// drain loop to pick them up — they MUST NOT spawn a parallel
    /// dispatcher (per-thread FIFO would break otherwise).
    /// </summary>
    public bool Dispatching { get; set; }
}
