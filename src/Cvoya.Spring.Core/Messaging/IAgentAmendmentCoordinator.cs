// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Units;

/// <summary>
/// Seam that encapsulates the mid-flight amendment concern extracted from
/// <c>AgentActor</c>: parsing the amendment payload, authorising the sender,
/// enqueueing the <see cref="PendingAmendment"/>, applying
/// <see cref="AmendmentPriority.StopAndWait"/> semantics, and emitting the
/// corresponding <see cref="ActivityEvent"/>s.
/// </summary>
/// <remarks>
/// <para>
/// The interface lives in <c>Cvoya.Spring.Core</c> so the cloud host can
/// substitute a tenant-aware coordinator (e.g. one that layers audit logging
/// on every amendment, enforces per-tenant rate limits, or gates amendments on
/// a permission model) without touching the actor. Per the platform's
/// "interface-first + TryAdd*" rule, production DI registers the default
/// implementation with <c>TryAddSingleton</c> so the private repo's
/// registration takes precedence when present.
/// </para>
/// <para>
/// The coordinator holds zero Dapr-actor references. <see cref="HandleAmendmentAsync"/>
/// receives delegate parameters so the actor can inject its own state-read,
/// state-write, cancellation, and activity-emission implementations without
/// the coordinator depending on Dapr actor types or scoped DI services.
/// </para>
/// <para>
/// Both <c>IUnitMembershipRepository</c> (used for sender authorisation) and
/// the active-work-cancellation source are scoped / per-actor — they are
/// passed as delegates so the coordinator can remain a singleton and share
/// safely across all <c>AgentActor</c> instances.
/// </para>
/// </remarks>
public interface IAgentAmendmentCoordinator
{
    /// <summary>
    /// Handles a mid-flight <see cref="MessageType.Amendment"/> message (#142).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The flow: parse the payload, authorise the sender (self-amendment or
    /// parent-unit membership), enqueue the <see cref="PendingAmendment"/> in
    /// actor state, emit <see cref="ActivityEventType.AmendmentReceived"/>, and
    /// — when <see cref="AmendmentPriority.StopAndWait"/> — cancel the active
    /// work and set the paused flag.
    /// </para>
    /// <para>
    /// Malformed payloads (missing or blank <see cref="AmendmentPayload.Text"/>)
    /// and unauthorised senders each emit
    /// <see cref="ActivityEventType.AmendmentRejected"/> and return without
    /// enqueueing. Disabled memberships are also dropped (with a rejection
    /// event) so senders cannot use the amendment channel as an enabled-flag
    /// probe.
    /// </para>
    /// </remarks>
    /// <param name="agentId">The Dapr actor id of the receiving agent.</param>
    /// <param name="message">
    /// The <see cref="MessageType.Amendment"/> message to process.
    /// </param>
    /// <param name="getMembership">
    /// Delegate that resolves the membership of a given unit for this agent.
    /// Returns <c>null</c> when no membership record exists. Passed as a
    /// delegate so the coordinator can remain a singleton even though
    /// <c>IUnitMembershipRepository</c> is scoped.
    /// </param>
    /// <param name="getPendingAmendments">
    /// Delegate that reads the current pending-amendment list from actor state.
    /// Returns an empty / no-value result when the list has never been set.
    /// </param>
    /// <param name="setPendingAmendments">
    /// Delegate that writes the updated pending-amendment list back to actor
    /// state after enqueueing.
    /// </param>
    /// <param name="cancelActiveWork">
    /// Delegate that cancels and disposes the actor's active-work
    /// <see cref="System.Threading.CancellationTokenSource"/> (if any). Called
    /// only for <see cref="AmendmentPriority.StopAndWait"/> amendments. The
    /// actor owns the CTS lifetime; the coordinator calls this delegate rather
    /// than holding a direct reference.
    /// </param>
    /// <param name="setPaused">
    /// Delegate that sets the <c>AgentPaused</c> state flag to <c>true</c>.
    /// Called only for <see cref="AmendmentPriority.StopAndWait"/> amendments,
    /// after <paramref name="cancelActiveWork"/>.
    /// </param>
    /// <param name="emitActivity">
    /// Delegate that publishes an <see cref="ActivityEvent"/> to the activity
    /// bus. Called for <see cref="ActivityEventType.AmendmentReceived"/> on
    /// acceptance and <see cref="ActivityEventType.AmendmentRejected"/> on any
    /// rejection path. Also called with
    /// <see cref="ActivityEventType.StateChanged"/> when stop-and-wait pauses
    /// the agent.
    /// </param>
    /// <param name="cancellationToken">Cancels the handle operation.</param>
    /// <returns>
    /// <c>true</c> when the amendment was accepted and enqueued;
    /// <c>false</c> when it was rejected (malformed, unauthorised, or
    /// disabled-membership drop).
    /// </returns>
    Task<bool> HandleAmendmentAsync(
        string agentId,
        Message message,
        Func<string, CancellationToken, Task<UnitMembership?>> getMembership,
        Func<CancellationToken, Task<(bool hasValue, List<PendingAmendment>? value)>> getPendingAmendments,
        Func<List<PendingAmendment>, CancellationToken, Task> setPendingAmendments,
        Func<Task> cancelActiveWork,
        Func<CancellationToken, Task> setPaused,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default);
}
