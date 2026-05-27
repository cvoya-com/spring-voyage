// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Outbound;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging;

/// <summary>
/// Bridges the platform's per-delivery
/// <see cref="IConnectorDeliveryObserver"/> seam to the Slack outbound
/// dispatcher (#2818). The platform's
/// <c>MessageDeliveryService.DeliverWithRetryAsync</c> invokes this
/// observer once per successful mailbox enqueue, with the full
/// envelope, the caller / target pair, and the participant set the
/// delivery's thread was resolved against. The observer hands that
/// context to <see cref="ISlackOutboundDispatcher"/>, which surfaces
/// the message in Slack when the thread has a Slack-bound participant
/// and silently returns <see cref="SlackOutboundResult.NoSlackSurface"/>
/// when it does not.
///
/// <para>
/// Domain messages only — control messages (<c>HealthCheck</c>,
/// <c>Cancel</c>, <c>StatusQuery</c>, …) are infrastructure plumbing
/// that never carries a user-visible payload; surfacing them on Slack
/// would just spam the operator's DM. Per
/// <see cref="IMessageWriter.ShouldWrite"/>, only <c>Domain</c> traffic
/// is persisted on the thread timeline; mirroring that filter here
/// keeps the Slack thread aligned with the SV-side conversation.
/// </para>
///
/// <para>
/// Per-target fan-out is the platform's responsibility — the platform
/// calls this observer once per delivery, so the observer never
/// iterates recipients. Per-thread ordering is preserved because the
/// platform calls observers inline after each successful enqueue.
/// </para>
/// </summary>
public sealed class SlackOutboundDeliveryObserver : IConnectorDeliveryObserver
{
    private readonly ISlackOutboundDispatcher _dispatcher;
    private readonly ILogger<SlackOutboundDeliveryObserver> _logger;

    /// <summary>Creates a new <see cref="SlackOutboundDeliveryObserver"/>.</summary>
    public SlackOutboundDeliveryObserver(
        ISlackOutboundDispatcher dispatcher,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _dispatcher = dispatcher;
        _logger = loggerFactory.CreateLogger<SlackOutboundDeliveryObserver>();
    }

    /// <inheritdoc />
    public async Task OnDeliveredAsync(
        Address caller,
        Address target,
        Message message,
        IReadOnlyCollection<Address> threadParticipants,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(threadParticipants);

        if (message.Type != MessageType.Domain)
        {
            // Control messages never surface on Slack — see remarks.
            return;
        }

        // The dispatcher accepts the participant set as IReadOnlyList; the
        // platform hands us a collection that is already materialised, so a
        // shallow copy into a list is cheap and predictable.
        var participants = threadParticipants as IReadOnlyList<Address>
            ?? threadParticipants.ToList();

        try
        {
            var result = await _dispatcher
                .DispatchAsync(message, participants, cancellationToken)
                .ConfigureAwait(false);

            if (result == SlackOutboundResult.Delivered)
            {
                _logger.LogDebug(
                    "Slack delivery observer: posted SV message {MessageId} to Slack (thread {ThreadId}).",
                    message.Id,
                    message.ThreadId ?? "(none)");
            }
        }
        catch (NotSupportedException ex)
        {
            // ADR-0061 §7.2 — PrivateChannel routing reserved for the hybrid
            // mode. The dispatcher throws on that branch; surfacing it as a
            // warning rather than letting it propagate keeps the platform's
            // delivery hot path unaffected by a connector-side limitation.
            _logger.LogWarning(
                ex,
                "Slack delivery observer: PrivateChannel routing not yet supported for SV message {MessageId}; skipping.",
                message.Id);
        }
    }
}
