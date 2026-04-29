// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;

/// <summary>
/// Seam that encapsulates the observation-channel and initiative-dispatch
/// concern extracted from <c>AgentActor</c>: recording observations into a
/// bounded in-state channel, registering the initiative reminder, draining
/// observations through <see cref="IInitiativeEngine"/>, dispatching the
/// resulting <see cref="ReflectionOutcome"/> through the
/// <see cref="IAgentInitiativeEvaluator"/> / <see cref="IReflectionActionHandlerRegistry"/>
/// pipeline, and emitting the corresponding activity events.
/// </summary>
/// <remarks>
/// <para>
/// The interface lives in <c>Cvoya.Spring.Core</c> so the cloud host can
/// substitute a tenant-aware coordinator (e.g. one that gates observation
/// recording on per-tenant rate limits or layers audit logging on every
/// reflection dispatch) without touching the actor. Per the platform's
/// "interface-first + TryAdd*" rule, production DI registers the default
/// implementation with <c>TryAddSingleton</c> so the private repo's
/// registration takes precedence when present.
/// </para>
/// <para>
/// The coordinator does not hold a reference to the actor. Instead, both
/// methods receive delegate parameters so the actor can inject its own
/// state-read, state-write, reminder-registration, activity-event, and
/// policy-evaluation implementations without the coordinator depending on
/// Dapr actor types or scoped DI services.
/// </para>
/// <para>
/// The coordinator is stateless with respect to any individual agent — it
/// operates entirely through the per-call delegates and the injected
/// singleton seams. This makes it safe to register as a singleton and
/// share across all <c>AgentActor</c> instances.
/// </para>
/// </remarks>
public interface IAgentObservationCoordinator
{
    /// <summary>
    /// Records <paramref name="observation"/> into the agent's bounded
    /// observation channel and arranges for the initiative reminder to fire.
    /// </summary>
    /// <param name="agentId">The Dapr actor id of the recording agent.</param>
    /// <param name="agentAddress">The <see cref="Address"/> of the recording agent.
    /// Surfaced as the message source when the coordinator later translates a
    /// <see cref="ReflectionOutcome"/> into a routable message.</param>
    /// <param name="observation">The observation payload to record.</param>
    /// <param name="getObservations">
    /// Delegate that reads the current observation list from actor state.
    /// </param>
    /// <param name="setObservations">
    /// Delegate that writes the updated observation list back to actor state.
    /// </param>
    /// <param name="registerReminder">
    /// Delegate that registers (or refreshes) the actor's initiative reminder.
    /// Called after the observation is persisted. The delegate owns the Dapr
    /// reminder lifetime; the coordinator does not import any Dapr actor APIs.
    /// </param>
    /// <param name="emitActivity">
    /// Delegate that publishes an <see cref="ActivityEvent"/> to the activity
    /// bus. Called once with an
    /// <see cref="ActivityEventType.InitiativeTriggered"/> event after the
    /// observation is persisted and the reminder is scheduled.
    /// </param>
    /// <param name="cancellationToken">Cancels the record operation.</param>
    Task RecordObservationAsync(
        string agentId,
        Address agentAddress,
        JsonElement observation,
        Func<CancellationToken, Task<List<JsonElement>>> getObservations,
        Func<List<JsonElement>, CancellationToken, Task> setObservations,
        Func<CancellationToken, Task> registerReminder,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drains the observation channel through <see cref="IInitiativeEngine"/>,
    /// evaluates the resulting <see cref="ReflectionOutcome"/> via the
    /// <see cref="IAgentInitiativeEvaluator"/> pipeline, and dispatches or
    /// proposes the action. Called by the actor inside
    /// <c>ReceiveReminderAsync</c> when the initiative reminder fires.
    /// </summary>
    /// <param name="agentId">The Dapr actor id of the acting agent.</param>
    /// <param name="agentAddress">The <see cref="Address"/> of the acting agent.
    /// Used as the <c>From</c> field of any message translated from a
    /// <see cref="ReflectionOutcome"/>.</param>
    /// <param name="getObservations">
    /// Delegate that reads the current observation list from actor state.
    /// Returns an empty/no-value result when the channel is empty so the
    /// coordinator can short-circuit without dispatching.
    /// </param>
    /// <param name="setObservations">
    /// Delegate that writes the (cleared) observation list back to actor state
    /// after a successful engine call.
    /// </param>
    /// <param name="evaluateSkillPolicy">
    /// Delegate that evaluates the unit's skill-invocation policy for a given
    /// action type. Returns a <see cref="PolicyDecision"/> indicating whether
    /// the action is allowed or blocked. Passed as a delegate (rather than an
    /// injected service) so the coordinator can remain a singleton even though
    /// <c>IUnitPolicyEnforcer</c> is scoped.
    /// </param>
    /// <param name="evaluateInitiative">
    /// Delegate that evaluates the initiative context and returns the dispatch
    /// decision (act autonomously, act with confirmation, defer). Passed as a
    /// delegate so the coordinator can remain a singleton even though
    /// <c>IAgentInitiativeEvaluator</c> is scoped.
    /// </param>
    /// <param name="emitActivity">
    /// Delegate that publishes an <see cref="ActivityEvent"/> to the activity
    /// bus. Called for <see cref="ActivityEventType.ReflectionCompleted"/>,
    /// <see cref="ActivityEventType.ReflectionActionDispatched"/>,
    /// <see cref="ActivityEventType.ReflectionActionProposed"/>, and
    /// <see cref="ActivityEventType.ReflectionActionSkipped"/> events.
    /// </param>
    /// <param name="cancellationToken">Cancels the check operation.</param>
    Task RunInitiativeCheckAsync(
        string agentId,
        Address agentAddress,
        Func<CancellationToken, Task<List<JsonElement>?>> getObservations,
        Func<List<JsonElement>, CancellationToken, Task> setObservations,
        Func<string, CancellationToken, Task<PolicyDecision>> evaluateSkillPolicy,
        Func<InitiativeEvaluationContext, CancellationToken, Task<InitiativeEvaluationResult>> evaluateInitiative,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default);
}