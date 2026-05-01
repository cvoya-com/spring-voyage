// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Observability;

/// <summary>
/// Materialises thread views from the observability layer. Threads
/// are not stored as a separate aggregate — they are derived from activity
/// events whose <c>CorrelationId</c> carries the thread id assigned by
/// the messaging layer. The service groups those events into the shapes the
/// CLI's <c>spring thread</c> and <c>spring inbox</c> verbs (plus the
/// matching portal surfaces) need.
/// </summary>
public interface IThreadQueryService
{
    /// <summary>
    /// Lists thread summaries matching the supplied filters, ordered by
    /// most-recent activity first. Returns an empty list when no activity
    /// events carry a non-null correlation id.
    /// </summary>
    /// <param name="filters">Optional filters; omitted fields match all threads.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<IReadOnlyList<ThreadSummary>> ListAsync(
        ThreadQueryFilters filters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets the full event log for a single thread — the summary row plus
    /// every activity event correlated to the thread id, ordered oldest
    /// first. Returns <c>null</c> when no events are found for the supplied id.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<ThreadDetail?> GetAsync(
        string threadId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists inbox rows for the supplied human address — threads where
    /// the human has a <c>MessageReceived</c> event and no non-human actor
    /// has observed a <c>MessageReceived</c> after that point on the same
    /// thread. Rows drop off when the human replies (and an agent /
    /// unit on the other side acknowledges the reply with its own
    /// <c>MessageReceived</c>). The predicate intentionally ignores trailing
    /// observability events such as <c>StateChanged</c> from dispatch
    /// teardown or <c>CostIncurred</c> from a budget enforcer (#1210).
    ///
    /// When <paramref name="lastReadAt"/> is supplied, each row's
    /// <see cref="InboxItem.UnreadCount"/> is set to the count of thread
    /// events whose timestamp is strictly greater than the stored
    /// <c>lastReadAt</c> for that thread. Missing entries mean "never read"
    /// (<see cref="DateTimeOffset.MinValue"/>), making all events count as
    /// unread.
    /// </summary>
    /// <param name="humanAddress">The <c>scheme://path</c> of the human whose inbox to load.</param>
    /// <param name="lastReadAt">
    /// Optional per-thread read cursor — maps <c>threadId</c> to the last
    /// time the human opened that thread. Pass <c>null</c> to skip unread
    /// computation (all rows get <c>UnreadCount = 0</c>).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<IReadOnlyList<InboxItem>> ListInboxAsync(
        string humanAddress,
        IReadOnlyDictionary<string, DateTimeOffset>? lastReadAt,
        CancellationToken cancellationToken);
}