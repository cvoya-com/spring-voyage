// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Observability;

/// <summary>
/// Materialises thread views from the EF-authoritative <c>threads</c> and
/// <c>messages</c> tables (per ADR-0030 / ADR-0040). The service groups those
/// rows into the shapes the CLI's <c>spring thread</c> and <c>spring inbox</c>
/// verbs (plus the matching portal surfaces) need. Cloud overlays may decorate
/// or replace the default implementation through DI without touching call
/// sites.
/// </summary>
public interface IThreadQueryService
{
    /// <summary>
    /// Lists thread summaries matching the supplied filters, ordered by
    /// most-recent activity first. Returns an empty list when no threads
    /// match.
    /// </summary>
    /// <param name="filters">Optional filters; omitted fields match all threads.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<IReadOnlyList<ThreadSummary>> ListAsync(
        ThreadQueryFilters filters,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets the full message timeline for a single thread — the summary row
    /// plus every persisted message on the thread, ordered oldest first.
    /// Returns <c>null</c> when no thread is found for the supplied id.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<ThreadDetail?> GetAsync(
        string threadId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists inbox rows for the supplied human address — threads where
    /// the human has received at least one message and has not replied
    /// since. The predicate is a single SQL aggregate per (tenant, thread)
    /// — "last received-by-human &gt; last sent-by-human" — so trailing
    /// observability events such as <c>StateChanged</c> on the underlying
    /// activity stream do not perturb the row (#1210). Rows drop off as
    /// soon as the human's own <see cref="Cvoya.Spring.Core.Messaging.Message"/>
    /// is dispatched on the thread.
    ///
    /// When <paramref name="lastReadAt"/> is supplied, each row's
    /// <see cref="InboxItem.UnreadCount"/> is set to the count of messages
    /// whose <c>sent_at</c> is strictly greater than the stored cursor.
    /// Missing entries mean "never read" (<see cref="DateTimeOffset.MinValue"/>),
    /// making every message count as unread.
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
