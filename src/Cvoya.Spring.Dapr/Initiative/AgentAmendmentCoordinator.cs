// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Initiative;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="IAgentAmendmentCoordinator"/>.
/// Owns the mid-flight amendment concern extracted from <c>AgentActor</c>:
/// parsing the payload, authorising the sender, enqueueing the
/// <see cref="PendingAmendment"/>, applying
/// <see cref="AmendmentPriority.StopAndWait"/> semantics, and emitting the
/// corresponding activity events.
/// </summary>
/// <remarks>
/// The coordinator is stateless with respect to any individual agent — it
/// operates entirely through the per-call delegates. This makes it safe to
/// register as a singleton and share across all <c>AgentActor</c> instances.
/// </remarks>
public class AgentAmendmentCoordinator(
    ILogger<AgentAmendmentCoordinator> logger) : IAgentAmendmentCoordinator
{
    /// <inheritdoc />
    public async Task<bool> HandleAmendmentAsync(
        string agentId,
        Message message,
        Func<string, CancellationToken, Task<UnitMembership?>> getMembership,
        Func<CancellationToken, Task<(bool hasValue, List<PendingAmendment>? value)>> getPendingAmendments,
        Func<List<PendingAmendment>, CancellationToken, Task> setPendingAmendments,
        Func<Task> cancelActiveWork,
        Func<CancellationToken, Task> setPaused,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default)
    {
        AmendmentPayload? payload;
        try
        {
            payload = message.Payload.Deserialize<AmendmentPayload>();
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Text))
        {
            await EmitAmendmentRejectedAsync(
                agentId, message, "MalformedPayload",
                "Amendment payload missing required Text field.",
                emitActivity, cancellationToken);
            return false;
        }

        // Authorisation: amendments are only accepted from the agent itself
        // or from a unit that contains this agent.
        var allowed = await IsAmendmentSenderAllowedAsync(
            agentId, message.From, getMembership, cancellationToken);

        if (!allowed.Allowed)
        {
            await EmitAmendmentRejectedAsync(
                agentId, message,
                allowed.Reason ?? "Rejected",
                allowed.Detail ?? "Amendment rejected.",
                emitActivity, cancellationToken);
            return false;
        }

        // Disabled memberships: log-and-drop. Returning an ack keeps the
        // sender from using the amendment channel as an enabled-flag probe.
        if (allowed.DisabledMembership)
        {
            logger.LogInformation(
                "Actor {ActorId} dropping amendment {MessageId} from {Sender}: membership Enabled=false.",
                agentId, message.Id, message.From);

            await EmitAmendmentRejectedAsync(
                agentId, message, "MembershipDisabled",
                "Amendment from a unit in which the agent is disabled.",
                emitActivity, cancellationToken);
            return false;
        }

        var pending = new PendingAmendment(
            Id: message.Id,
            From: message.From,
            Text: payload.Text,
            Priority: payload.Priority,
            CorrelationId: payload.CorrelationId ?? message.ThreadId,
            ReceivedAt: DateTimeOffset.UtcNow);

        await EnqueueAmendmentAsync(
            pending, getPendingAmendments, setPendingAmendments, cancellationToken);

        await emitActivity(
            BuildEvent(
                agentId,
                ActivityEventType.AmendmentReceived,
                ActivitySeverity.Info,
                $"Amendment accepted from {message.From.Scheme}://{message.From.Path} at priority {payload.Priority}.",
                details: JsonSerializer.SerializeToElement(new
                {
                    messageId = message.Id,
                    priority = payload.Priority.ToString(),
                    correlationId = pending.CorrelationId,
                    text = payload.Text,
                }),
                correlationId: pending.CorrelationId),
            cancellationToken);

        if (payload.Priority == AmendmentPriority.StopAndWait)
        {
            await ApplyStopAndWaitAsync(agentId, cancelActiveWork, setPaused, emitActivity, cancellationToken);
        }

        return true;
    }

    /// <summary>
    /// Decides whether <paramref name="sender"/> is permitted to amend the
    /// agent identified by <paramref name="agentId"/>. Returns structured
    /// output so the caller can distinguish between rejection categories when
    /// emitting activity events.
    /// </summary>
    private async Task<AmendmentAuthorisation> IsAmendmentSenderAllowedAsync(
        string agentId,
        Address sender,
        Func<string, CancellationToken, Task<UnitMembership?>> getMembership,
        CancellationToken cancellationToken)
    {
        // Self-amendment: the agent addresses itself. Always allowed.
        if (string.Equals(sender.Scheme, "agent", StringComparison.Ordinal) &&
            string.Equals(sender.Path, agentId, StringComparison.Ordinal))
        {
            return AmendmentAuthorisation.Allow();
        }

        // Only parent units may amend. All other schemes are rejected.
        if (!string.Equals(sender.Scheme, "unit", StringComparison.Ordinal))
        {
            return AmendmentAuthorisation.Reject("NotAMember",
                $"Sender {sender.Scheme}://{sender.Path} is not a parent unit or the agent itself.");
        }

        UnitMembership? membership;
        try
        {
            membership = await getMembership(sender.Path, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Repository failure is treated as reject so a stale/broken
            // membership store cannot widen the amendment surface.
            logger.LogWarning(ex,
                "Membership lookup failed evaluating amendment sender {Sender} for agent {AgentId}; rejecting.",
                sender, agentId);
            return AmendmentAuthorisation.Reject("MembershipLookupFailed", ex.Message);
        }

        if (membership is null)
        {
            return AmendmentAuthorisation.Reject("NotAMember",
                $"Agent is not a member of unit '{sender.Path}'.");
        }

        return new AmendmentAuthorisation(
            Allowed: true,
            DisabledMembership: membership.Enabled == false,
            Reason: null,
            Detail: null);
    }

    private static async Task EnqueueAmendmentAsync(
        PendingAmendment pending,
        Func<CancellationToken, Task<(bool hasValue, List<PendingAmendment>? value)>> getPendingAmendments,
        Func<List<PendingAmendment>, CancellationToken, Task> setPendingAmendments,
        CancellationToken cancellationToken)
    {
        var (hasValue, existing) = await getPendingAmendments(cancellationToken);
        var list = hasValue && existing is not null ? existing : new List<PendingAmendment>();
        list.Add(pending);
        await setPendingAmendments(list, cancellationToken);
    }

    private async Task ApplyStopAndWaitAsync(
        string agentId,
        Func<Task> cancelActiveWork,
        Func<CancellationToken, Task> setPaused,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken)
    {
        await cancelActiveWork();
        await setPaused(cancellationToken);

        logger.LogInformation(
            "Actor {ActorId} paused via StopAndWait amendment.",
            agentId);

        await emitActivity(
            BuildEvent(
                agentId,
                ActivityEventType.StateChanged,
                ActivitySeverity.Debug,
                "State changed to Paused awaiting clarification.",
                details: JsonSerializer.SerializeToElement(new { from = "Active", to = "Paused" })),
            cancellationToken);
    }

    private Task EmitAmendmentRejectedAsync(
        string agentId,
        Message message,
        string reason,
        string detail,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken)
    {
        var details = JsonSerializer.SerializeToElement(new
        {
            reason,
            detail,
            sender = new { scheme = message.From.Scheme, path = message.From.Path },
            messageId = message.Id,
        });

        return emitActivity(
            BuildEvent(
                agentId,
                ActivityEventType.AmendmentRejected,
                ActivitySeverity.Info,
                $"Amendment rejected from {message.From.Scheme}://{message.From.Path}: {reason}",
                details: details,
                correlationId: message.ThreadId),
            cancellationToken);
    }

    private static ActivityEvent BuildEvent(
        string agentId,
        ActivityEventType eventType,
        ActivitySeverity severity,
        string summary,
        JsonElement? details = null,
        string? correlationId = null)
    {
        return new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            Address.For("agent", agentId),
            eventType,
            severity,
            summary,
            details,
            correlationId);
    }

    /// <summary>
    /// Internal result type for amendment-sender authorisation.
    /// </summary>
    private readonly record struct AmendmentAuthorisation(
        bool Allowed,
        bool DisabledMembership,
        string? Reason,
        string? Detail)
    {
        public static AmendmentAuthorisation Allow() => new(true, false, null, null);

        public static AmendmentAuthorisation Reject(string reason, string detail) =>
            new(false, false, reason, detail);
    }
}
