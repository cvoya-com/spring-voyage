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
    /// Free-text search across persisted messages, scoped to the supplied
    /// participant. Returns matching messages (newest match first), each
    /// annotated with its containing thread id so the caller can stitch
    /// the hit back to a thread. Implementations should prefer a Postgres
    /// full-text path where available and fall back to a case-insensitive
    /// substring scan elsewhere (the in-memory test provider falls into
    /// the latter bucket).
    /// </summary>
    /// <param name="participant">
    /// Canonical address of the caller. Only threads where this address
    /// appears in the persisted participant set are searched — search is
    /// scoped to what the caller is allowed to see.
    /// </param>
    /// <param name="query">Free-text query string. Empty input yields no rows.</param>
    /// <param name="threadId">
    /// Optional thread filter. When supplied, the search is constrained to
    /// the single thread (and only when the caller is a participant of it).
    /// </param>
    /// <param name="limit">Maximum number of rows to return.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<IReadOnlyList<ThreadSearchHit>> SearchAsync(
        string participant,
        string query,
        string? threadId,
        int limit,
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
