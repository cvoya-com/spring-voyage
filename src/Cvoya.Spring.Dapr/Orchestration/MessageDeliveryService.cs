// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Routing;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Delivery acknowledgement for a single <c>sv.messaging.send</c>
/// message-delivery tool call (ADR-0049). It confirms the message was
/// durably placed in the recipient's mailbox — it never carries the
/// recipient's response.
/// </summary>
public sealed record MessageDeliveryAck(
    bool Delivered,
    Guid MessageId,
    Address Target,
    Guid ThreadId);

/// <summary>
/// Per-target delivery outcome for a <c>sv.messaging.broadcast</c> call
/// (ADR-0049). Reports whether the message reached each recipient's mailbox
/// — not the recipients' work products.
/// </summary>
public sealed record BroadcastTargetAck(
    Address Target,
    bool Delivered,
    string? Error);

/// <summary>
/// Shared delivery seam for the platform messaging tools (ADR-0048 /
/// ADR-0049). The platform delivers messages; it does not orchestrate.
/// <see cref="MessagingToolHandlers"/> uses this service to validate the
/// caller / target, bound the per-thread hop count, and synchronously
/// deliver a message to one or many mailboxes with bounded retry.
/// </summary>
public class MessageDeliveryService(
    IAgentProxyResolver agentProxyResolver,
    IActorProxyFactory actorProxyFactory,
    IOrchestrationTenantResolver tenantResolver,
    ILogger<MessageDeliveryService> logger,
    IOptions<OrchestrationDeliveryOptions>? deliveryOptions = null)
{
    private readonly OrchestrationDeliveryOptions _deliveryOptions =
        deliveryOptions?.Value ?? new OrchestrationDeliveryOptions();

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
            throw new OrchestrationException(
                OrchestrationException.RejectCodes.OrchestrationCrossTenant,
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
            throw new OrchestrationException(
                OrchestrationException.RejectCodes.OrchestrationCrossTenant,
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
            throw new OrchestrationException(
                OrchestrationException.RejectCodes.OrchestrationSelfDelegation,
                $"Caller '{caller}' cannot deliver a message to itself.");
        }
    }

    /// <summary>
    /// Increments the per-thread message-delivery hop counter (#2576) and
    /// throws <see cref="OrchestrationException"/> with
    /// <see cref="OrchestrationException.RejectCodes.OrchestrationDepthExceeded"/>
    /// when the new count exceeds
    /// <see cref="OrchestrationDeliveryOptions.MaxHopCount"/>. Called once per
    /// <c>sv.messaging.send</c> / <c>sv.messaging.broadcast</c> call before
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
            throw new OrchestrationException(
                OrchestrationException.RejectCodes.OrchestrationDepthExceeded,
                $"Thread '{GuidFormatter.Format(threadId)}' exceeded the message-delivery hop limit " +
                $"of {_deliveryOptions.MaxHopCount} (hop {hopCount}). The conversation is likely in a " +
                "delivery cycle; no further messages will be delivered on this thread.");
        }
    }

    /// <summary>
    /// Delivers a message to one target with bounded retry (ADR-0049 §4),
    /// returning a per-target outcome rather than throwing on a transient
    /// failure. Used by the broadcast path so one failed target does not
    /// abort the rest.
    /// </summary>
    public async Task<BroadcastTargetAck> DeliverOutcomeAsync(
        Address caller,
        Address target,
        Message message,
        CancellationToken ct)
    {
        try
        {
            await DeliverWithRetryAsync(caller, target, message, ct);
            return new BroadcastTargetAck(target, true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new BroadcastTargetAck(target, false, ex.Message);
        }
    }

    /// <summary>
    /// Delivers a message to one target with bounded retry (ADR-0049 §4). A
    /// successful <c>ReceiveAsync</c> is a durable mailbox enqueue and the
    /// fast-enqueue invariant (§5) means it returns in milliseconds — the
    /// loop blocks only on the enqueue, never on the recipient's runtime.
    /// Only transient infrastructure failures are retried; an
    /// <see cref="OrchestrationException"/> (validation) is never retried and
    /// propagates immediately. A retry budget exhausted by transient failures
    /// surfaces as a terminal <see cref="OrchestrationException"/>.
    /// </summary>
    public async Task DeliverWithRetryAsync(
        Address caller,
        Address target,
        Message message,
        CancellationToken ct)
    {
        var proxy = agentProxyResolver.Resolve(target.Scheme, GuidFormatter.Format(target.Id))
            ?? throw new OrchestrationException(
                OrchestrationException.RejectCodes.OrchestrationDeliveryFailed,
                $"Could not resolve message-delivery target '{target}'.");

        var outbound = message with
        {
            From = caller,
            To = target,
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
            catch (OrchestrationException)
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

        throw new OrchestrationException(
            OrchestrationException.RejectCodes.OrchestrationDeliveryFailed,
            $"Delivery of message '{message.Id}' to '{target}' failed after " +
            $"{_deliveryOptions.MaxAttempts} attempt(s) within the delivery budget.",
            lastTransient ?? new InvalidOperationException("Delivery failed."));
    }

    private static bool AddressEquals(Address left, Address right) =>
        left.Id == right.Id &&
        string.Equals(left.Scheme, right.Scheme, StringComparison.OrdinalIgnoreCase);
}
