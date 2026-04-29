// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;

/// <summary>
/// Seam that encapsulates the thread-mailbox routing concern extracted from
/// <c>AgentActor</c>: routing domain messages to the active or pending thread
/// channel, enqueueing messages for pending threads, promoting the next pending
/// thread to active, and suspending the currently active thread.
/// </summary>
/// <remarks>
/// <para>
/// The interface lives in <c>Cvoya.Spring.Core</c> so the cloud host can
/// substitute a tenant-aware coordinator (e.g. one that layers audit logging,
/// per-tenant thread-count enforcement, or custom promotion ordering) without
/// touching the actor. Per the platform's "interface-first + TryAdd*" rule,
/// production DI registers the default implementation with
/// <c>TryAddSingleton</c> so the private repo's registration takes precedence
/// when present.
/// </para>
/// <para>
/// The coordinator holds zero Dapr-actor references. Both methods receive
/// delegate parameters so the actor injects its own state-read, state-write,
/// cancellation, dispatch, and activity-emission implementations without the
/// coordinator depending on Dapr actor types or scoped DI services.
/// </para>
/// <para>
/// The coordinator is stateless with respect to any individual agent — it
/// operates entirely through the per-call delegates and the injected
/// singleton seams. This makes it safe to register as a singleton and share
/// across all <c>AgentActor</c> instances.
/// </para>
/// </remarks>
public interface IAgentMailboxCoordinator
{
    /// <summary>
    /// Routes a domain <paramref name="message"/> to the correct thread channel
    /// after pre-conditions (membership, policy) have been resolved by the
    /// actor. Implements three cases:
    /// <list type="bullet">
    /// <item><description>
    /// Case 1 — no active thread: creates a new <see cref="ThreadChannel"/>,
    /// activates it, and fires the dispatch task via
    /// <paramref name="activateAndDispatch"/>.
    /// </description></item>
    /// <item><description>
    /// Case 2 — message belongs to the already-active thread: appends the
    /// message to the channel and returns.
    /// </description></item>
    /// <item><description>
    /// Case 3 — different thread already active: routes to
    /// <see cref="EnqueuePendingMessageAsync"/> and emits a
    /// <see cref="ActivityEventType.DecisionMade"/> event.
    /// </description></item>
    /// </list>
    /// </summary>
    /// <param name="agentId">The Dapr actor id of the routing agent.</param>
    /// <param name="message">The validated domain message to route. Must have a non-null <c>ThreadId</c>.</param>
    /// <param name="effective">
    /// The per-turn effective <see cref="AgentMetadata"/> already resolved and
    /// policy-checked by the actor. The coordinator uses it only to forward it
    /// to <paramref name="activateAndDispatch"/>; it does not re-evaluate policy.
    /// </param>
    /// <param name="getActiveConversation">
    /// Delegate that reads the current active <see cref="ThreadChannel"/> from
    /// actor state. Returns <c>null</c> when no thread is active.
    /// </param>
    /// <param name="setActiveConversation">
    /// Delegate that writes a new or updated <see cref="ThreadChannel"/> as the
    /// active conversation in actor state.
    /// </param>
    /// <param name="getPendingList">
    /// Delegate that reads the pending <see cref="ThreadChannel"/> list from
    /// actor state. Returns <c>null</c> or an empty list when no threads are
    /// pending.
    /// </param>
    /// <param name="setPendingList">
    /// Delegate that writes the updated pending list back to actor state.
    /// </param>
    /// <param name="activateAndDispatch">
    /// Delegate called in Case 1 to set up the actor's
    /// <see cref="System.Threading.CancellationTokenSource"/>, build the prompt
    /// assembly context, and start the fire-and-forget dispatch task.
    /// Receives the newly created channel, the resolved effective metadata, and
    /// a cancellation token.
    /// </param>
    /// <param name="emitActivity">
    /// Delegate that publishes an <see cref="ActivityEvent"/> to the activity
    /// bus. Called for thread-start, state-change, and queued-as-pending events.
    /// </param>
    /// <param name="cancellationToken">Cancels the routing operation.</param>
    Task HandleDomainMessageAsync(
        string agentId,
        Message message,
        AgentMetadata effective,
        Func<CancellationToken, Task<ThreadChannel?>> getActiveConversation,
        Func<ThreadChannel, CancellationToken, Task> setActiveConversation,
        Func<CancellationToken, Task<List<ThreadChannel>?>> getPendingList,
        Func<List<ThreadChannel>, CancellationToken, Task> setPendingList,
        Func<ThreadChannel, AgentMetadata, CancellationToken, Task> activateAndDispatch,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enqueues <paramref name="message"/> into the pending channel for
    /// <paramref name="threadId"/>, creating the channel if it does not yet
    /// exist in the pending list.
    /// </summary>
    /// <param name="agentId">The Dapr actor id of the queueing agent.</param>
    /// <param name="threadId">The thread id the message belongs to.</param>
    /// <param name="message">The message to enqueue.</param>
    /// <param name="getPendingList">
    /// Delegate that reads the pending <see cref="ThreadChannel"/> list from
    /// actor state.
    /// </param>
    /// <param name="setPendingList">
    /// Delegate that writes the updated pending list back to actor state.
    /// </param>
    /// <param name="cancellationToken">Cancels the enqueue operation.</param>
    Task EnqueuePendingMessageAsync(
        string agentId,
        string threadId,
        Message message,
        Func<CancellationToken, Task<List<ThreadChannel>?>> getPendingList,
        Func<List<ThreadChannel>, CancellationToken, Task> setPendingList,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Promotes the head of the pending <see cref="ThreadChannel"/> list to the
    /// active slot and fires a dispatch task for its first message.
    /// Does nothing when the pending list is empty.
    /// </summary>
    /// <param name="agentId">The Dapr actor id of the promoting agent.</param>
    /// <param name="getPendingList">
    /// Delegate that reads the pending list from actor state.
    /// </param>
    /// <param name="setPendingList">
    /// Delegate that writes the remaining pending list back to actor state after
    /// the head is removed.
    /// </param>
    /// <param name="removePendingList">
    /// Delegate that removes the pending-list key from actor state entirely when
    /// the list becomes empty after promotion.
    /// </param>
    /// <param name="setActiveConversation">
    /// Delegate that writes the newly promoted channel as the active conversation
    /// in actor state.
    /// </param>
    /// <param name="activateAndDispatch">
    /// Delegate called to set up the actor's cancellation source, build the
    /// prompt assembly context for the promoted thread's head message, and start
    /// the fire-and-forget dispatch task. Receives the promoted channel, the
    /// resolved effective metadata (from <paramref name="resolveEffectiveMetadata"/>),
    /// and a cancellation token. Passed as a delegate so the coordinator can
    /// remain a singleton even though the underlying operations depend on
    /// actor-scoped state.
    /// </param>
    /// <param name="resolveEffectiveMetadata">
    /// Delegate that resolves the per-turn effective <see cref="AgentMetadata"/>
    /// for a given <see cref="Message"/>. Called against the promoted thread's
    /// first message before dispatch. Passed as a delegate so the coordinator
    /// does not hold a reference to scoped services.
    /// </param>
    /// <param name="cancellationToken">Cancels the promotion operation.</param>
    Task PromoteNextPendingAsync(
        string agentId,
        Func<CancellationToken, Task<List<ThreadChannel>?>> getPendingList,
        Func<List<ThreadChannel>, CancellationToken, Task> setPendingList,
        Func<CancellationToken, Task> removePendingList,
        Func<ThreadChannel, CancellationToken, Task> setActiveConversation,
        Func<ThreadChannel, AgentMetadata, CancellationToken, Task> activateAndDispatch,
        Func<Message, CancellationToken, Task<AgentMetadata>> resolveEffectiveMetadata,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Suspends the currently active <see cref="ThreadChannel"/> by cancelling
    /// any in-flight work, moving the active channel to the head of the pending
    /// list, and clearing the active-conversation state slot.
    /// Does nothing when no conversation is active.
    /// </summary>
    /// <param name="agentId">The Dapr actor id of the suspending agent.</param>
    /// <param name="getActiveConversation">
    /// Delegate that reads the current active <see cref="ThreadChannel"/> from
    /// actor state.
    /// </param>
    /// <param name="removeActiveConversation">
    /// Delegate that removes the active-conversation state key.
    /// </param>
    /// <param name="getPendingList">
    /// Delegate that reads the pending list from actor state.
    /// </param>
    /// <param name="setPendingList">
    /// Delegate that writes the updated pending list (with the previously active
    /// channel appended) back to actor state.
    /// </param>
    /// <param name="cancelActiveWork">
    /// Delegate that cancels and disposes the actor's current
    /// <see cref="System.Threading.CancellationTokenSource"/> (if any). The
    /// actor owns the CancellationTokenSource lifetime; the coordinator calls
    /// this delegate rather than holding a direct reference.
    /// </param>
    /// <param name="emitActivity">
    /// Delegate that publishes an <see cref="ActivityEvent"/>. Called with a
    /// <see cref="ActivityEventType.StateChanged"/> event once the active thread
    /// is moved to pending.
    /// </param>
    /// <param name="cancellationToken">Cancels the suspension operation.</param>
    Task SuspendActiveConversationAsync(
        string agentId,
        Func<CancellationToken, Task<ThreadChannel?>> getActiveConversation,
        Func<CancellationToken, Task> removeActiveConversation,
        Func<CancellationToken, Task<List<ThreadChannel>?>> getPendingList,
        Func<List<ThreadChannel>, CancellationToken, Task> setPendingList,
        Func<Task> cancelActiveWork,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default);
}