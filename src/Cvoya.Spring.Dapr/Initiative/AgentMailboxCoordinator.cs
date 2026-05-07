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
/// Owns the thread-mailbox routing concern extracted from <c>AgentActor</c>:
/// routing domain messages to the active or pending thread channel, enqueueing
/// messages for pending threads, promoting the next pending thread to active,
/// and suspending the currently active thread.
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
        Func<CancellationToken, Task<ThreadChannel?>> getActiveConversation,
        Func<ThreadChannel, CancellationToken, Task> setActiveConversation,
        Func<CancellationToken, Task<List<ThreadChannel>?>> getPendingList,
        Func<List<ThreadChannel>, CancellationToken, Task> setPendingList,
        Func<ThreadChannel, AgentMetadata, CancellationToken, Task> activateAndDispatch,
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

        var activeConversation = await getActiveConversation(cancellationToken);

        // Case 1: No active thread — make this the active one and dispatch.
        if (activeConversation is null)
        {
            var channel = new ThreadChannel
            {
                ThreadId = threadId,
                Messages = [message],
                CreatedAt = DateTimeOffset.UtcNow,
            };

            await setActiveConversation(channel, cancellationToken);

            logger.LogInformation("Actor {ActorId} activated thread {ThreadId}",
                agentId, threadId);

            await emitActivity(
                BuildEvent(
                    agentId,
                    ActivityEventType.ThreadStarted,
                    ActivitySeverity.Info,
                    $"Started thread {threadId}",
                    correlationId: threadId),
                cancellationToken);

            await emitActivity(
                BuildEvent(
                    agentId,
                    ActivityEventType.StateChanged,
                    ActivitySeverity.Debug,
                    "State changed from Idle to Active",
                    details: System.Text.Json.JsonSerializer.SerializeToElement(new { from = "Idle", to = "Active" })),
                cancellationToken);

            await activateAndDispatch(channel, effective, cancellationToken);
            return;
        }

        // Case 2: Message belongs to the active thread — append to it.
        if (activeConversation.ThreadId == threadId)
        {
            activeConversation.Messages.Add(message);
            await setActiveConversation(activeConversation, cancellationToken);

            logger.LogInformation("Actor {ActorId} appended message to active thread {ThreadId}",
                agentId, threadId);

            return;
        }

        // Case 3: Different thread — route to pending.
        await EnqueuePendingMessageAsync(
            agentId,
            threadId,
            message,
            getPendingList,
            setPendingList,
            cancellationToken);

        logger.LogInformation("Actor {ActorId} queued message for pending thread {ThreadId}",
            agentId, threadId);

        await emitActivity(
            BuildEvent(
                agentId,
                ActivityEventType.DecisionMade,
                ActivitySeverity.Info,
                $"Queued thread {threadId} as pending (active: {activeConversation.ThreadId})",
                details: System.Text.Json.JsonSerializer.SerializeToElement(new
                {
                    decision = "QueueAsPending",
                    activeThreadId = activeConversation.ThreadId,
                    pendingThreadId = threadId,
                }),
                correlationId: threadId),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task EnqueuePendingMessageAsync(
        string agentId,
        string threadId,
        Message message,
        Func<CancellationToken, Task<List<ThreadChannel>?>> getPendingList,
        Func<List<ThreadChannel>, CancellationToken, Task> setPendingList,
        CancellationToken cancellationToken = default)
    {
        var pending = await getPendingList(cancellationToken);
        var pendingList = pending ?? [];

        var existingChannel = pendingList.Find(c => c.ThreadId == threadId);
        if (existingChannel is not null)
        {
            existingChannel.Messages.Add(message);
        }
        else
        {
            pendingList.Add(new ThreadChannel
            {
                ThreadId = threadId,
                Messages = [message],
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await setPendingList(pendingList, cancellationToken);
    }

    /// <inheritdoc />
    public async Task PromoteNextPendingAsync(
        string agentId,
        Func<CancellationToken, Task<List<ThreadChannel>?>> getPendingList,
        Func<List<ThreadChannel>, CancellationToken, Task> setPendingList,
        Func<CancellationToken, Task> removePendingList,
        Func<ThreadChannel, CancellationToken, Task> setActiveConversation,
        Func<ThreadChannel, AgentMetadata, CancellationToken, Task> activateAndDispatch,
        Func<Message, CancellationToken, Task<AgentMetadata>> resolveEffectiveMetadata,
        CancellationToken cancellationToken = default)
    {
        var pending = await getPendingList(cancellationToken);

        if (pending is null || pending.Count == 0)
        {
            return;
        }

        var next = pending[0];
        pending.RemoveAt(0);

        await setActiveConversation(next, cancellationToken);

        if (pending.Count > 0)
        {
            await setPendingList(pending, cancellationToken);
        }
        else
        {
            await removePendingList(cancellationToken);
        }

        logger.LogInformation("Actor {ActorId} promoted thread {ThreadId} to active",
            agentId, next.ThreadId);

        // Promotion alone is not enough: the queued head message must be
        // dispatched, otherwise the actor sits Active-but-idle and the
        // user never gets a response. The first-message path in
        // HandleDomainMessageAsync (Case 1) explicitly schedules
        // RunDispatchAsync after activating; promotion has to do the
        // same. Discovered post-Stage-2 cutover (#1063 / #522 follow-up):
        // after a previous turn was cleared, the queued thread
        // got "promoted" but never dispatched until the user sent a new
        // message, and even then it stayed in Case 2 (append) which
        // also doesn't dispatch.
        if (next.Messages is { Count: > 0 } messages)
        {
            var head = messages[0];
            try
            {
                var effective = await resolveEffectiveMetadata(head, cancellationToken);
                if (effective.Enabled == false)
                {
                    logger.LogInformation(
                        "Actor {ActorId} skipping promoted thread {ThreadId}: membership Enabled=false.",
                        agentId, next.ThreadId);
                    return;
                }

                await activateAndDispatch(next, effective, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Actor {ActorId} failed to dispatch promoted thread {ThreadId}.",
                    agentId, next.ThreadId);
            }
        }
    }

    /// <inheritdoc />
    public async Task SuspendActiveConversationAsync(
        string agentId,
        Func<CancellationToken, Task<ThreadChannel?>> getActiveConversation,
        Func<CancellationToken, Task> removeActiveConversation,
        Func<CancellationToken, Task<List<ThreadChannel>?>> getPendingList,
        Func<List<ThreadChannel>, CancellationToken, Task> setPendingList,
        Func<Task> cancelActiveWork,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default)
    {
        var activeConversation = await getActiveConversation(cancellationToken);

        if (activeConversation is null)
        {
            return;
        }

        await cancelActiveWork();

        var pending = await getPendingList(cancellationToken);
        var pendingList = pending ?? [];
        pendingList.Add(activeConversation);

        await setPendingList(pendingList, cancellationToken);
        await removeActiveConversation(cancellationToken);

        logger.LogInformation("Actor {ActorId} suspended thread {ThreadId}",
            agentId, activeConversation.ThreadId);

        await emitActivity(
            BuildEvent(
                agentId,
                ActivityEventType.StateChanged,
                ActivitySeverity.Debug,
                "State changed from Active to Suspended",
                details: System.Text.Json.JsonSerializer.SerializeToElement(new { from = "Active", to = "Suspended" })),
            cancellationToken);
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