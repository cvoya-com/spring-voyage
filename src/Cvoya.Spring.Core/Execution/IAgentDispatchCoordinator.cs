// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Seam that encapsulates the execution-dispatch concern extracted from
/// <c>AgentActor</c>: invoking the <see cref="IExecutionDispatcher"/>,
/// observing the runtime's lifecycle outcome, emitting per-phase activity
/// events (<see cref="ActivityEventType.MessageDispatchedToRuntime"/>,
/// <see cref="ActivityEventType.RuntimeStarted"/>, and the terminal
/// <see cref="ActivityEventType.RuntimeCompleted"/> /
/// <see cref="ActivityEventType.RuntimeFailed"/> /
/// <see cref="ActivityEventType.RuntimeCompletedSilent"/>), and signalling
/// the per-thread dispatch exit so the actor's mailbox can drain remaining
/// queued messages or mark the channel idle.
/// </summary>
/// <remarks>
/// <para>
/// Per <see href="../../../docs/decisions/0056-tool-only-side-effects.md">ADR-0056</see>
/// the coordinator no longer persists a dispatch "response" — there is no
/// such thing. Every observable effect a runtime has flows through platform
/// tool calls (the messaging tool persists its own row, etc.); the
/// coordinator's role narrows to <em>"invoke dispatcher, observe outcome,
/// emit lifecycle activities, signal exit"</em>.
/// </para>
/// <para>
/// The interface lives in <c>Cvoya.Spring.Core</c> so the cloud host can
/// substitute a tenant-aware coordinator (e.g. one that layers audit logging,
/// per-tenant cost attribution, or custom retry logic) without touching the
/// actor. Per the platform's "interface-first + TryAdd*" rule, production DI
/// registers the default implementation with <c>TryAddSingleton</c> so the
/// private repo's registration takes precedence when present.
/// </para>
/// <para>
/// The coordinator holds zero Dapr-actor references. <see cref="RunDispatchAsync"/>
/// receives delegate parameters so the actor can inject its own
/// activity-emission and per-thread dispatch-exit implementations without
/// the coordinator depending on Dapr actor types or scoped DI services.
/// </para>
/// <para>
/// The coordinator is stateless with respect to any individual agent — it
/// operates entirely through the per-call delegates and the injected singleton
/// seams. This makes it safe to register as a singleton and share across all
/// <c>AgentActor</c> instances.
/// </para>
/// </remarks>
public interface IAgentDispatchCoordinator
{
    /// <summary>
    /// Runs the execution dispatcher for a single agent turn, emits the
    /// per-phase lifecycle activities, and signals the per-thread dispatch
    /// exit. The exit callback runs on every termination path (success,
    /// cancel, exception, or non-zero container exit) so the actor's
    /// mailbox can drain any messages appended while the dispatcher was
    /// running, or mark the channel idle when the queue is empty.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method runs outside the Dapr actor turn (fire-and-forget), so
    /// implementations MUST NOT touch actor state directly via
    /// <c>StateManager</c>. State mutations on the per-thread channel are
    /// routed through <paramref name="onDispatchExit"/> so the actor can
    /// schedule them as a self-call, which queues the mutation on the
    /// actor's own turn queue.
    /// </para>
    /// <para>
    /// The terminal activity is exactly one of
    /// <see cref="ActivityEventType.RuntimeCompleted"/>,
    /// <see cref="ActivityEventType.RuntimeFailed"/>, or
    /// <see cref="ActivityEventType.RuntimeCompletedSilent"/>. The "silent"
    /// flavour
    /// (<see href="../../../docs/decisions/0056-tool-only-side-effects.md">ADR-0056</see>
    /// §5) is emitted when the runtime exited cleanly but invoked no
    /// platform tool calls — the platform tolerates this compliance gap
    /// rather than auto-wrapping the terminal text into a synthesised
    /// message.
    /// </para>
    /// </remarks>
    /// <param name="agentId">
    /// The Dapr actor id (<c>Id.GetId()</c>) of the dispatching agent. Used
    /// for structured log correlation and activity events.
    /// </param>
    /// <param name="message">
    /// The domain message that triggered the dispatch. Provides thread-id
    /// context for routing and log messages.
    /// </param>
    /// <param name="context">
    /// The prompt-assembly context assembled by the actor before starting the
    /// dispatch task. Forwarded unchanged to
    /// <see cref="IExecutionDispatcher.DispatchAsync"/>.
    /// </param>
    /// <param name="emitActivity">
    /// Delegate that publishes an <see cref="ActivityEvent"/> to the activity
    /// bus. Called for the runtime-lifecycle events (started, terminal,
    /// reasoning) plus any error rows on the cancel / exception paths.
    /// Passed as a delegate so the coordinator can remain a singleton even
    /// though the actor's own <c>EmitActivityEventAsync</c> captures
    /// per-instance fields.
    /// </param>
    /// <param name="onDispatchExit">
    /// Delegate that signals the per-thread dispatch exit. Receives a
    /// reason string explaining why the dispatcher returned (success,
    /// cancel, non-zero exit, exception). The actor's implementation
    /// drains any messages that arrived for this thread while the
    /// dispatcher was running, or marks the channel idle when the queue
    /// is empty. Per-ADR-0030, the exit is per-thread — other threads on
    /// the same agent are unaffected.
    /// </param>
    /// <param name="cancellationToken">
    /// The cancellation token tied to the actor's per-thread cancellation
    /// source. When this token is cancelled the coordinator logs the
    /// cancellation and calls <paramref name="onDispatchExit"/> before
    /// returning.
    /// </param>
    /// <param name="batch">
    /// The full ordered set of pending messages delivered to the runtime in
    /// this single turn (#3056), oldest-first. <paramref name="message"/> is
    /// the representative (<c>batch[0]</c>) used for routing / correlation /
    /// lifecycle activities; the whole <paramref name="batch"/> is forwarded
    /// to <see cref="IExecutionDispatcher.DispatchAsync"/> so the inbound
    /// envelope can name every message in the set. <c>null</c> (or a
    /// single-element list) means a one-message turn — identical to the
    /// pre-#3056 behaviour.
    /// </param>
    /// <param name="costAttributionAgentId">
    /// The agent id the turn's cost should bill against, when it differs from
    /// <paramref name="agentId"/> (issue #3075). For an ephemeral clone, the
    /// actor passes the parent agent id so the clone's spend rolls up to the
    /// parent's cost rollup rather than being stranded under the short-lived
    /// clone id. <c>null</c> (the common case) bills the dispatching agent
    /// itself.
    /// </param>
    Task RunDispatchAsync(
        string agentId,
        Message message,
        PromptAssemblyContext context,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        Func<string, Task> onDispatchExit,
        CancellationToken cancellationToken = default,
        IReadOnlyList<Message>? batch = null,
        string? costAttributionAgentId = null);
}
