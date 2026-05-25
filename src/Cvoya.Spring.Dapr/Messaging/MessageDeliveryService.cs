// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Messaging;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Routing;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Per-target outcome returned by
/// <see cref="MessageDeliveryService.DeliverOutcomeAsync"/>. Reports
/// whether the message reached the recipient's mailbox; the recipient's
/// reply, if any, arrives later as a fresh inbound message (ADR-0049).
/// </summary>
public sealed record DeliveryOutcome(
    Address Target,
    bool Delivered,
    string? Error);

/// <summary>
/// Shared delivery seam for the platform messaging tools (ADR-0048 /
/// ADR-0049). The platform delivers messages; it does not orchestrate.
/// <see cref="MessagingToolHandlers"/> uses this service to validate the
/// caller / target, bound the per-thread hop count, and synchronously
/// deliver a message to one or many mailboxes with bounded retry.
/// <para>
/// Each delivery resolves its own thread from the <c>(caller, target)</c>
/// participant set via <see cref="IThreadRegistry"/> (#2596 / ADR-0030).
/// A message delivered by <c>sv.messaging.send</c> / <c>sv.messaging.multicast</c>
/// carries the thread of <i>that</i> hop's participant set — it never
/// inherits the caller's upstream <see cref="Message.ThreadId"/>. The actor
/// mailbox keys its per-thread FIFO channel and dispatch-cancellation scope
/// on this id, so a distinct participant set must resolve to a distinct
/// thread or unrelated conversations collide.
/// </para>
/// </summary>
public class MessageDeliveryService(
    IAgentProxyResolver agentProxyResolver,
    IActorProxyFactory actorProxyFactory,
    IMessageTenantResolver tenantResolver,
    IServiceScopeFactory scopeFactory,
    ILogger<MessageDeliveryService> logger,
    IOptions<MessageDeliveryOptions>? deliveryOptions = null)
{
    private readonly MessageDeliveryOptions _deliveryOptions =
        deliveryOptions?.Value ?? new MessageDeliveryOptions();

    /// <summary>
    /// ADR-0039 §3 gate 6 — cross-tenant containment, caller side. The
    /// validated callback token claims a tenant; the resolved tenant of the
    /// caller must match.
    /// </summary>
    public async Task EnsureCallerTenantAsync(
        Address caller,
        Guid expectedTenantId,
        CancellationToken ct)
    {
        var resolved = await tenantResolver.GetTenantForAddressAsync(caller, ct);
        if (resolved != expectedTenantId)
        {
            throw new MessageDeliveryException(
                MessageDeliveryException.RejectCodes.CrossTenant,
                $"Caller '{caller}' belongs to tenant '{GuidFormatter.Format(resolved)}' " +
                $"but the callback token claims tenant '{GuidFormatter.Format(expectedTenantId)}'.");
        }
    }

    /// <summary>
    /// ADR-0039 §3 gate 6 — cross-tenant containment, target side.
    /// Evaluating the gate on the target prevents a directory-level bug that
    /// crosses a tenant boundary from leaking through the messaging surface.
    /// </summary>
    public async Task EnsureTargetTenantAsync(
        Address target,
        Guid expectedTenantId,
        CancellationToken ct)
    {
        var resolved = await tenantResolver.GetTenantForAddressAsync(target, ct);
        if (resolved != expectedTenantId)
        {
            throw new MessageDeliveryException(
                MessageDeliveryException.RejectCodes.CrossTenant,
                $"Target '{target}' belongs to tenant '{GuidFormatter.Format(resolved)}' " +
                $"but the callback token claims tenant '{GuidFormatter.Format(expectedTenantId)}'.");
        }
    }

    /// <summary>
    /// Rejects a caller that addresses itself — a message-delivery tool may
    /// not deliver to its own mailbox.
    /// </summary>
    public static void EnsureNotSelfTarget(Address caller, Address target)
    {
        if (AddressEquals(caller, target))
        {
            throw new MessageDeliveryException(
                MessageDeliveryException.RejectCodes.SelfDelivery,
                $"Caller '{caller}' cannot deliver a message to itself.");
        }
    }

    /// <summary>
    /// Rejects a target whose scheme is non-routable. The
    /// <see cref="Address.ConnectorScheme"/> is the sole v0.1 case (#2747):
    /// connectors stamp message provenance on inbound webhook events but do
    /// not host an actor mailbox and cannot receive messages.
    /// </summary>
    public static void EnsureCanReceive(Address target)
    {
        if (string.Equals(target.Scheme, Address.ConnectorScheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new MessageDeliveryException(
                MessageDeliveryException.RejectCodes.UnroutableTarget,
                $"Cannot deliver to '{target}': the '{Address.ConnectorScheme}' scheme is non-routable. " +
                "Connectors are senders only — they translate external events into inbound messages " +
                "but do not receive replies. Pick a routable participant (agent, unit, or human).");
        }
    }

    /// <summary>
    /// Increments the per-thread message-delivery hop counter (#2576) and
    /// throws <see cref="MessageDeliveryException"/> with
    /// <see cref="MessageDeliveryException.RejectCodes.DepthExceeded"/>
    /// when the new count exceeds
    /// <see cref="MessageDeliveryOptions.MaxHopCount"/>. Called once per
    /// <c>sv.messaging.send</c> / <c>sv.messaging.multicast</c> call before
    /// any delivery attempt, so a fan-out cycle terminates at the limit.
    /// </summary>
    public async Task EnsureHopBudgetAsync(Guid threadId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var hopActor = actorProxyFactory.CreateActorProxy<IThreadHopActor>(
            new ActorId(GuidFormatter.Format(threadId)),
            nameof(ThreadHopActor));

        var hopCount = await hopActor.IncrementAsync();
        if (hopCount > _deliveryOptions.MaxHopCount)
        {
            throw new MessageDeliveryException(
                MessageDeliveryException.RejectCodes.DepthExceeded,
                $"Thread '{GuidFormatter.Format(threadId)}' exceeded the message-delivery hop limit " +
                $"of {_deliveryOptions.MaxHopCount} (hop {hopCount}). The conversation is likely in a " +
                "delivery cycle; no further messages will be delivered on this thread.");
        }
    }

    /// <summary>
    /// Delivers a message to one target with bounded retry (ADR-0049 §4),
    /// returning a per-target outcome rather than throwing on a transient
    /// failure. Used by the multicast path so one failed target does not
    /// abort the rest. The optional <paramref name="threadParticipants"/>
    /// overrides the default <c>(caller, target)</c> participant set so a
    /// shared-thread <c>sv.messaging.send</c> places every recipient's
    /// delivery on the same thread (#2747).
    /// </summary>
    public async Task<DeliveryOutcome> DeliverOutcomeAsync(
        Address caller,
        Address target,
        Message message,
        CancellationToken ct,
        IReadOnlyCollection<Address>? threadParticipants = null)
    {
        try
        {
            await DeliverWithRetryAsync(caller, target, message, ct, threadParticipants);
            return new DeliveryOutcome(target, true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DeliveryOutcome(target, false, ex.Message);
        }
    }

    /// <summary>
    /// Delivers a message to one target with bounded retry (ADR-0049 §4). A
    /// successful <c>ReceiveAsync</c> is a durable mailbox enqueue and the
    /// fast-enqueue invariant (§5) means it returns in milliseconds — the
    /// loop blocks only on the enqueue, never on the recipient's runtime.
    /// Only transient infrastructure failures are retried; an
    /// <see cref="MessageDeliveryException"/> (validation) is never retried and
    /// propagates immediately. A retry budget exhausted by transient failures
    /// surfaces as a terminal <see cref="MessageDeliveryException"/>.
    /// <para>
    /// <paramref name="threadParticipants"/> overrides the per-hop default
    /// <c>{caller, target}</c> participant set. <c>sv.messaging.multicast</c>
    /// omits it (each recipient lands on its own 1-1 thread);
    /// <c>sv.messaging.send</c> with multiple recipients passes the full
    /// <c>{caller} ∪ recipients</c> set so every recipient's delivery is
    /// keyed onto the same shared thread (#2747).
    /// </para>
    /// </summary>
    public async Task DeliverWithRetryAsync(
        Address caller,
        Address target,
        Message message,
        CancellationToken ct,
        IReadOnlyCollection<Address>? threadParticipants = null)
    {
        var proxy = agentProxyResolver.Resolve(target.Scheme, GuidFormatter.Format(target.Id))
            ?? throw new MessageDeliveryException(
                MessageDeliveryException.RejectCodes.DeliveryFailed,
                $"Could not resolve message-delivery target '{target}'.");

        // #2596 / ADR-0030 — resolve the thread from the participant set so
        // the outbound message lands on a thread distinct from the caller's
        // upstream conversation. The default is the (caller, target) hop;
        // #2747 lets sv.messaging.send share one thread across all recipients
        // by passing the full participant set in threadParticipants.
        var threadId = threadParticipants is { Count: > 0 }
            ? await ResolveThreadAsync(threadParticipants, ct)
            : await ResolveHopThreadAsync(caller, target, ct);

        var outbound = message with
        {
            From = caller,
            To = target,
            ThreadId = threadId,
        };

        var deadline = DateTimeOffset.UtcNow + _deliveryOptions.Budget;
        var backoff = _deliveryOptions.InitialBackoff;
        Exception? lastTransient = null;

        for (var attempt = 1; attempt <= _deliveryOptions.MaxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                await proxy.ReceiveAsync(outbound, ct);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (MessageDeliveryException)
            {
                // Validation rejection from the recipient — never transient.
                throw;
            }
            catch (Exception ex)
            {
                // ADR-0049 §4 — the only thing that can fail a mailbox
                // enqueue is transient infrastructure. Retry within budget.
                lastTransient = ex;
                logger.LogWarning(
                    ex,
                    "Transient delivery failure for message {MessageId} to {Target} (attempt {Attempt}/{MaxAttempts}).",
                    message.Id,
                    target,
                    attempt,
                    _deliveryOptions.MaxAttempts);
            }

            if (attempt >= _deliveryOptions.MaxAttempts)
            {
                break;
            }

            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            var delay = backoff < remaining ? backoff : remaining;
            await Task.Delay(delay, ct);
            backoff += backoff;
        }

        throw new MessageDeliveryException(
            MessageDeliveryException.RejectCodes.DeliveryFailed,
            $"Delivery of message '{message.Id}' to '{target}' failed after " +
            $"{_deliveryOptions.MaxAttempts} attempt(s) within the delivery budget.",
            lastTransient ?? new InvalidOperationException("Delivery failed."));
    }

    /// <summary>
    /// Resolves the thread id for one delivery hop from its
    /// <c>(caller, target)</c> participant set (#2596 / ADR-0030). The same
    /// pair always resolves to the same thread; a different pair — a
    /// different target, a different caller — produces a different set, hence
    /// a different thread. <see cref="IThreadRegistry"/> is scoped (EF-backed)
    /// while this service is a singleton, so a DI scope is opened per
    /// delivery, matching the pattern <see cref="MessagingToolHandlers"/>
    /// uses for its own scoped collaborators.
    /// </summary>
    private async Task<string> ResolveHopThreadAsync(
        Address caller,
        Address target,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var threadRegistry = scope.ServiceProvider.GetRequiredService<IThreadRegistry>();
        return await threadRegistry.GetOrCreateAsync(new[] { caller, target }, ct);
    }

    /// <summary>
    /// Resolves the thread id for an explicit participant set. The set may
    /// include the caller and any subset of {agent, unit, human} recipients.
    /// Order is irrelevant — <see cref="IThreadRegistry"/> canonicalises.
    /// </summary>
    private async Task<string> ResolveThreadAsync(
        IReadOnlyCollection<Address> participants,
        CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var threadRegistry = scope.ServiceProvider.GetRequiredService<IThreadRegistry>();
        return await threadRegistry.GetOrCreateAsync(participants, ct);
    }

    private static bool AddressEquals(Address left, Address right) =>
        left.Id == right.Id &&
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase);
}
