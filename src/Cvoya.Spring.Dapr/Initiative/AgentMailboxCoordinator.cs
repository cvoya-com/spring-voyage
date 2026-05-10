// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Initiative;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="IAgentMailboxCoordinator"/>.
/// Owns the per-thread channel routing concern (#2076 / ADR-0030 §3 §44):
/// finding or creating the channel for an inbound message's thread,
/// appending the message, and dispatching when no drain loop is currently
/// running for that thread.
/// </summary>
/// <remarks>
/// The coordinator is stateless with respect to any individual agent — it
/// operates entirely through the per-call delegates. This makes it safe to
/// register as a singleton and share across all <c>AgentActor</c> instances.
/// </remarks>
public class AgentMailboxCoordinator(
    ILogger<AgentMailboxCoordinator> logger) : IAgentMailboxCoordinator
{
    /// <inheritdoc />
    public async Task HandleDomainMessageAsync(
        string agentId,
        Message message,
        AgentMetadata effective,
        Func<AgentMetadata, CancellationToken, Task<(AgentMetadata Effective, PolicyVerdict? Verdict)>> applyUnitPolicies,
        Func<string, CancellationToken, Task<ThreadChannel?>> getChannel,
        Func<ThreadChannel, CancellationToken, Task> saveChannel,
        Func<ThreadChannel, AgentMetadata, CancellationToken, Task> dispatch,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default)
    {
        var threadId = message.ThreadId!; // Validated by caller before entering the coordinator.

        // Guard 0: membership-disabled check (#1349). When the effective
        // metadata indicates the membership is disabled, emit a DecisionMade
        // event and return without routing. This guard was previously in
        // AgentActor.HandleDomainMessageAsync; it lives here so the actor
        // contains only message-dispatch logic.
        if (effective.Enabled == false)
        {
            logger.LogInformation(
                "Actor {ActorId} skipping message {MessageId} from {Sender}: membership Enabled=false.",
                agentId, message.Id, message.From);

            await emitActivity(
                BuildEvent(
                    agentId,
                    ActivityEventType.DecisionMade,
                    ActivitySeverity.Info,
                    $"Skipped message {message.Id} from {message.From}: membership disabled.",
                    details: System.Text.Json.JsonSerializer.SerializeToElement(new
                    {
                        decision = "MembershipDisabled",
                        sender = new { scheme = message.From.Scheme, path = message.From.Path },
                        messageId = message.Id,
                    }),
                    correlationId: threadId),
                cancellationToken);

            return;
        }

        // Guard 1: unit-policy check (#1349). Delegate to the actor-supplied
        // applyUnitPolicies function so the coordinator remains stateless and
        // agnostic of IAgentUnitPolicyCoordinator. When a non-null verdict is
        // returned, the dispatch is refused.
        (effective, var policyVerdict) = await applyUnitPolicies(effective, cancellationToken);
        if (policyVerdict is not null)
        {
            await emitActivity(
                BuildEvent(
                    agentId,
                    ActivityEventType.DecisionMade,
                    ActivitySeverity.Info,
                    $"Skipped message {message.Id} from {message.From}: {policyVerdict.Summary}.",
                    details: System.Text.Json.JsonSerializer.SerializeToElement(new
                    {
                        decision = policyVerdict.DecisionTag,
                        dimension = policyVerdict.Dimension,
                        reason = policyVerdict.Decision.Reason,
                        denyingUnitId = policyVerdict.Decision.DenyingUnitId,
                        messageId = message.Id,
                    }),
                    correlationId: threadId),
                cancellationToken);

            return;
        }

        var channel = await getChannel(threadId, cancellationToken);

        // Case 1: no existing channel — create one, mark dispatching, save,
        // and fire the dispatcher. This is the only emit site for
        // ThreadStarted; per ADR-0030 §44 the agent does not transition
        // through an "Idle → Active" agent-level state on first inbound,
        // because under concurrent threads such a transition is no longer
        // well-defined (the agent may be Active on N other threads).
        if (channel is null)
        {
            channel = new ThreadChannel
            {
                ThreadId = threadId,
                Messages = [message],
                CreatedAt = DateTimeOffset.UtcNow,
                Dispatching = true,
            };

            await saveChannel(channel, cancellationToken);

            logger.LogInformation("Actor {ActorId} created channel for thread {ThreadId}",
                agentId, threadId);

            await emitActivity(
                BuildEvent(
                    agentId,
                    ActivityEventType.ThreadStarted,
                    ActivitySeverity.Info,
                    $"Started thread {threadId}",
                    correlationId: threadId),
                cancellationToken);

            await dispatch(channel, effective, cancellationToken);
            return;
        }

        // Case 2: channel exists and a drain loop is already running. Append
        // the message and rely on the drain loop to pick it up at its next
        // iteration — per-thread FIFO is preserved.
        if (channel.Dispatching)
        {
            channel.Messages.Add(message);
            await saveChannel(channel, cancellationToken);

            logger.LogInformation(
                "Actor {ActorId} appended message to in-flight thread {ThreadId} (queue depth {Depth})",
                agentId, threadId, channel.Messages.Count);

            return;
        }

        // Case 3: channel exists but no drain loop is currently running. Re-mark
        // dispatching, append the message, save, and fire a fresh dispatcher
        // for the head message. This path is hit when a thread's dispatcher
        // completed successfully and the channel was kept around until the
        // next inbound on the same thread (the drain loop exits when the
        // queue is empty; a fresh inbound on the same thread restarts it).
        channel.Messages.Add(message);
        channel.Dispatching = true;
        await saveChannel(channel, cancellationToken);

        logger.LogInformation(
            "Actor {ActorId} restarted dispatch for thread {ThreadId}",
            agentId, threadId);

        await dispatch(channel, effective, cancellationToken);
    }

    private static ActivityEvent BuildEvent(
        string agentId,
        ActivityEventType eventType,
        ActivitySeverity severity,
        string summary,
        System.Text.Json.JsonElement? details = null,
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
}
