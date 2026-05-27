// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connectors;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Observes successful message deliveries (#2818). A connector that
/// must surface an outbound SV message on an external surface — Slack
/// posts the message as a threaded reply, future connectors may post
/// to Discord / Teams / SMS, etc. — registers an
/// <see cref="IConnectorDeliveryObserver"/> so the platform calls it
/// once per delivery <em>after</em> the recipient's mailbox enqueue
/// succeeds.
///
/// <para>
/// The platform invokes every registered observer from
/// <c>MessageDeliveryService.DeliverWithRetryAsync</c> after
/// <c>proxy.ReceiveAsync</c> returns, with the full delivery context
/// (caller, target, message envelope, thread participants). Observers
/// are best-effort — an observer that throws is logged and the next
/// observer is invoked, but the platform's delivery contract is
/// already satisfied. The observer is called inline (not on a
/// background task) so that per-thread ordering is preserved across
/// observers; an observer that needs to do slow work should fan out
/// internally.
/// </para>
///
/// <para>
/// This is the platform's seam between the message-delivery hot path
/// and connector-side outbound surfaces. Implementations live in the
/// connector projects; the platform never depends on a specific
/// connector. Observers must NOT participate in the delivery decision
/// — they are notified after the fact and cannot block, reject, or
/// re-route a delivery that has already landed.
/// </para>
/// </summary>
public interface IConnectorDeliveryObserver
{
    /// <summary>
    /// Notifies the observer that <paramref name="message"/> has been
    /// successfully enqueued into <paramref name="target"/>'s mailbox.
    /// </summary>
    /// <param name="caller">
    /// The originating sender's routable address (the one stamped on
    /// <see cref="Message.From"/> by the platform's outbound
    /// construction site).
    /// </param>
    /// <param name="target">
    /// The recipient's routable address. Never a
    /// <c>connector://</c>-scheme address (the delivery service rejects
    /// those before reaching the retry loop).
    /// </param>
    /// <param name="message">
    /// The full outbound envelope as it was enqueued — the same
    /// instance the recipient's actor receives.
    /// </param>
    /// <param name="threadParticipants">
    /// The participant set the delivery's thread was resolved against.
    /// For a 1-1 send this is <c>{caller, target}</c>; for a
    /// shared-thread <c>sv.messaging.send</c> with multiple recipients
    /// this is the full <c>{caller} ∪ recipients</c> set; for a
    /// per-pair <c>sv.messaging.multicast</c> delivery this is the
    /// pair <c>{caller, target}</c> the per-recipient thread was keyed
    /// against.
    /// </param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    Task OnDeliveredAsync(
        Address caller,
        Address target,
        Message message,
        IReadOnlyCollection<Address> threadParticipants,
        CancellationToken cancellationToken);
}
