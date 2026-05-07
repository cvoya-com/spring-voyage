// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Seam that encapsulates the execution-dispatch concern extracted from
/// <c>AgentActor</c>: invoking the <see cref="IExecutionDispatcher"/>,
/// inspecting the response for a non-zero container exit code, routing the
/// response message back to the caller, and clearing the active thread slot
/// via a Dapr self-call when the dispatch terminates abnormally.
/// </summary>
/// <remarks>
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
/// activity-emission and active-conversation-clearing implementations without
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
    /// Runs the execution dispatcher for a single agent turn, routes the
    /// response, and clears the active thread slot when the dispatch
    /// terminates abnormally (cancelled, exception, or non-zero container
    /// exit code).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This method runs outside the Dapr actor turn (fire-and-forget), so
    /// implementations MUST NOT touch actor state directly via
    /// <c>StateManager</c>. State mutations on failure are routed through
    /// <paramref name="clearActiveConversation"/> so the actor can schedule
    /// them as a self-call, which queues the mutation on the actor's own
    /// turn queue.
    /// </para>
    /// <para>
    /// A non-zero container exit (see the <c>ExitCode</c> / <c>Error</c>
    /// payload fields introduced by #1036) is treated as an abnormal
    /// termination: the error is surfaced to the caller via
    /// <paramref name="emitActivity"/> and the response is still routed
    /// (best-effort) before clearing the active thread.
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
    /// bus. Called on error-occurred events (non-zero exit, dispatch exception).
    /// Passed as a delegate so the coordinator can remain a singleton even
    /// though the actor's own <c>EmitActivityEventAsync</c> captures
    /// per-instance fields.
    /// </param>
    /// <param name="clearActiveConversation">
    /// Delegate that clears the active-conversation slot for the agent.
    /// Called with a reason string whenever the dispatch terminates abnormally
    /// (cancelled, exception, or non-zero exit). The actor owns the
    /// self-call / direct-call decision (production vs. test harness); the
    /// coordinator only invokes this delegate.
    /// </param>
    /// <param name="cancellationToken">
    /// The cancellation token tied to the actor's active-work CTS. When this
    /// token is cancelled the coordinator logs the cancellation and calls
    /// <paramref name="clearActiveConversation"/> before returning.
    /// </param>
    Task RunDispatchAsync(
        string agentId,
        Message message,
        PromptAssemblyContext context,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        Func<string, Task> clearActiveConversation,
        CancellationToken cancellationToken = default);
}