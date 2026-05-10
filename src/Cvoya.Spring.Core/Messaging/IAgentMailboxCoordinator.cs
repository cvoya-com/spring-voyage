// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Policies;

/// <summary>
/// Seam that encapsulates the per-thread-channel routing concern extracted
/// from <c>AgentActor</c> (#2076 / ADR-0030 §3 §44): finding or creating the
/// channel for an inbound message's thread, appending the message, and
/// launching a dispatcher when no drain loop is currently running for that
/// thread.
/// </summary>
/// <remarks>
/// <para>
/// The interface lives in <c>Cvoya.Spring.Core</c> so the cloud host can
/// substitute a tenant-aware coordinator (e.g. one that layers audit
/// logging, per-tenant per-thread depth caps, or custom membership-policy
/// enforcement) without touching the actor. Per the platform's
/// "interface-first + TryAdd*" rule, production DI registers the default
/// implementation with <c>TryAddSingleton</c> so the private repo's
/// registration takes precedence when present.
/// </para>
/// <para>
/// The coordinator holds zero Dapr-actor references. The single method
/// receives delegate parameters so the actor injects its own per-thread
/// channel reads / writes, dispatch, and activity-emission implementations
/// without the coordinator depending on Dapr actor types or scoped DI
/// services.
/// </para>
/// <para>
/// The coordinator is stateless with respect to any individual agent — it
/// operates entirely through the per-call delegates and the injected
/// singleton seams. This makes it safe to register as a singleton and
/// share across all <c>AgentActor</c> instances.
/// </para>
/// </remarks>
public interface IAgentMailboxCoordinator
{
    /// <summary>
    /// Routes a domain <paramref name="message"/> to its per-thread channel.
    /// Two pre-routing guards run before the channel update:
    /// <list type="bullet">
    /// <item><description>
    /// Guard 0 — membership disabled: if <paramref name="effective"/>.Enabled is
    /// <c>false</c>, emits a <see cref="ActivityEventType.DecisionMade"/>
    /// "MembershipDisabled" event and returns without routing (#1349).
    /// </description></item>
    /// <item><description>
    /// Guard 1 — unit-policy check: calls <paramref name="applyUnitPolicies"/>;
    /// if it returns a non-<c>null</c> <see cref="PolicyVerdict"/>, emits a
    /// <see cref="ActivityEventType.DecisionMade"/> "BlockedByUnitPolicy"
    /// event and returns without routing (#1349).
    /// </description></item>
    /// </list>
    /// <para>
    /// After the guards: the coordinator reads the channel for the inbound
    /// thread via <paramref name="getChannel"/>; if absent, it creates a
    /// fresh <see cref="ThreadChannel"/> with the message queued, marks it
    /// <see cref="ThreadChannel.Dispatching"/>, and persists it via
    /// <paramref name="saveChannel"/>; then it fires
    /// <paramref name="dispatch"/> for the head message. If the channel
    /// already exists and is mid-drain (<see cref="ThreadChannel.Dispatching"/>
    /// <c>= true</c>) the message is appended and the drain loop picks it
    /// up at its next iteration. If the channel exists but is idle (the
    /// drain loop exited and is waiting for new traffic), the coordinator
    /// re-marks it dispatching and fires a fresh dispatcher for the head
    /// message. Per-thread FIFO is preserved within each channel; threads
    /// run concurrently across channels per ADR-0030 §44.
    /// </para>
    /// </summary>
    /// <param name="agentId">The Dapr actor id of the routing agent.</param>
    /// <param name="message">The validated domain message to route. Must have a non-null <c>ThreadId</c>.</param>
    /// <param name="effective">
    /// The per-turn effective <see cref="AgentMetadata"/> resolved by the actor
    /// (merge of global config and per-membership override). The coordinator checks
    /// <c>Enabled</c> (Guard 0) and forwards the possibly-coerced value from
    /// <paramref name="applyUnitPolicies"/> to <paramref name="dispatch"/>.
    /// </param>
    /// <param name="applyUnitPolicies">
    /// Delegate that applies unit-level policy dimensions (model cap, cost cap,
    /// execution-mode coercion) to <paramref name="effective"/>. Returns the
    /// possibly-coerced metadata plus a non-<c>null</c>
    /// <see cref="PolicyVerdict"/> when the dispatch must be refused
    /// (Guard 1). Passed as a delegate so the coordinator remains a
    /// singleton and does not hold actor-scoped or Dapr-specific
    /// references (#1349).
    /// </param>
    /// <param name="getChannel">
    /// Delegate that reads the <see cref="ThreadChannel"/> for the supplied
    /// thread id from actor state. Returns <c>null</c> when no channel
    /// exists yet for that thread.
    /// </param>
    /// <param name="saveChannel">
    /// Delegate that persists a new or updated <see cref="ThreadChannel"/>
    /// to actor state, keyed by <see cref="ThreadChannel.ThreadId"/>. Also
    /// updates the channel-index entry so the actor can enumerate its
    /// active channels for status queries.
    /// </param>
    /// <param name="dispatch">
    /// Delegate the coordinator calls when a fresh drain loop must be
    /// launched. Receives the channel and the resolved effective metadata.
    /// The actor's implementation builds the prompt-assembly context,
    /// stamps a per-thread cancellation source, and starts the
    /// fire-and-forget dispatcher.
    /// </param>
    /// <param name="emitActivity">
    /// Delegate that publishes an <see cref="ActivityEvent"/> to the activity
    /// bus. Called for Guard 0 / Guard 1 rejections and for the
    /// <see cref="ActivityEventType.ThreadStarted"/> event when a brand-new
    /// channel is created.
    /// </param>
    /// <param name="cancellationToken">Cancels the routing operation.</param>
    Task HandleDomainMessageAsync(
        string agentId,
        Message message,
        AgentMetadata effective,
        Func<AgentMetadata, CancellationToken, Task<(AgentMetadata Effective, PolicyVerdict? Verdict)>> applyUnitPolicies,
        Func<string, CancellationToken, Task<ThreadChannel?>> getChannel,
        Func<ThreadChannel, CancellationToken, Task> saveChannel,
        Func<ThreadChannel, AgentMetadata, CancellationToken, Task> dispatch,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default);
}
