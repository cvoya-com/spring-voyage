// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Initiative;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="IAgentObservationCoordinator"/>.
/// Owns the observation-channel and initiative-dispatch concern extracted from
/// <c>AgentActor</c>: recording observations into a bounded channel, draining
/// observations through <see cref="IInitiativeEngine"/>, evaluating the
/// resulting <see cref="ReflectionOutcome"/> via the
/// <see cref="IAgentInitiativeEvaluator"/> / <see cref="IReflectionActionHandlerRegistry"/>
/// pipeline, and emitting the corresponding activity events.
/// </summary>
/// <remarks>
/// The coordinator is stateless with respect to any individual agent — it
/// operates entirely through the per-call delegates and the injected
/// singletons. This makes it safe to register as a singleton and share
/// across all <c>AgentActor</c> instances.
/// </remarks>
public class AgentObservationCoordinator(
    IInitiativeEngine initiativeEngine,
    IReflectionActionHandlerRegistry reflectionActionHandlers,
    IMessageRouter messageRouter,
    IAgentDefinitionProvider agentDefinitionProvider,
    ILogger<AgentObservationCoordinator> logger) : IAgentObservationCoordinator
{
    /// <summary>
    /// Maximum number of observations retained in the observation channel.
    /// Older entries are trimmed when the list exceeds this bound.
    /// </summary>
    internal const int MaxObservationChannelEntries = 100;

    /// <inheritdoc />
    public async Task RecordObservationAsync(
        string agentId,
        Address agentAddress,
        JsonElement observation,
        Func<CancellationToken, Task<List<JsonElement>>> getObservations,
        Func<List<JsonElement>, CancellationToken, Task> setObservations,
        Func<CancellationToken, Task> registerReminder,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default)
    {
        var list = await getObservations(cancellationToken);
        list.Add(observation);

        // Bound the list to the most recent MaxObservationChannelEntries.
        if (list.Count > MaxObservationChannelEntries)
        {
            list.RemoveRange(0, list.Count - MaxObservationChannelEntries);
        }

        await setObservations(list, cancellationToken);
        await registerReminder(cancellationToken);

        var summary = SummarizeObservation(observation);
        await emitActivity(
            BuildEvent(
                agentAddress,
                ActivityEventType.InitiativeTriggered,
                ActivitySeverity.Info,
                $"Observation recorded: {summary}"),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task RunInitiativeCheckAsync(
        string agentId,
        Address agentAddress,
        Func<CancellationToken, Task<List<JsonElement>?>> getObservations,
        Func<List<JsonElement>, CancellationToken, Task> setObservations,
        Func<string, CancellationToken, Task<PolicyDecision>> evaluateSkillPolicy,
        Func<InitiativeEvaluationContext, CancellationToken, Task<InitiativeEvaluationResult>> evaluateInitiative,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default)
    {
        var observations = await getObservations(cancellationToken);

        if (observations is null || observations.Count == 0)
        {
            return;
        }

        // Resolve the agent's real instructions from the canonical
        // AgentDefinition store. Plumbed through so the engine's
        // screening / reflection contexts reason against the agent's
        // actual role description rather than a synthesised stand-in
        // (#1617). A definition lookup failure is non-fatal: the engine
        // substitutes a documented fallback string for missing
        // instructions.
        string? agentInstructions = null;
        try
        {
            var definition = await agentDefinitionProvider.GetByIdAsync(agentId, cancellationToken);
            agentInstructions = definition?.Instructions;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to resolve agent definition for {AgentId}; engine will use fallback instructions.",
                agentId);
        }

        ReflectionOutcome? outcome;
        try
        {
            outcome = await initiativeEngine.ProcessObservationsAsync(
                agentId, observations, agentInstructions, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Initiative engine threw for agent {AgentId}; retaining observations for next tick.",
                agentId);
            return;
        }

        // Only clear observations after a successful engine call.
        await setObservations([], cancellationToken);

        if (outcome is null || !outcome.ShouldAct)
        {
            return;
        }

        var details = JsonSerializer.SerializeToElement(new
        {
            actionType = outcome.ActionType,
            reasoning = outcome.Reasoning,
            actionPayload = outcome.ActionPayload,
        });

        await emitActivity(
            BuildEvent(
                agentAddress,
                ActivityEventType.ReflectionCompleted,
                ActivitySeverity.Info,
                $"Reflection decided to act: {outcome.ActionType ?? "(unknown)"}",
                details: details),
            cancellationToken);

        await DispatchReflectionActionAsync(agentId, agentAddress, outcome, observations, evaluateSkillPolicy, evaluateInitiative, emitActivity, cancellationToken);
    }

    /// <summary>
    /// Translates a <see cref="ReflectionOutcome"/> into a concrete message,
    /// gates it through the initiative-evaluator seam
    /// (<see cref="IAgentInitiativeEvaluator"/>), and routes it via
    /// <see cref="IMessageRouter"/>. The evaluator is the single source of
    /// truth for initiative-specific composed enforcement (unit
    /// initiative-action allow / block list, cost caps, boundary / hierarchy
    /// permissions / cloning as they come online) — this caller must not
    /// re-run those gates.
    /// </summary>
    private async Task DispatchReflectionActionAsync(
        string agentId,
        Address agentAddress,
        ReflectionOutcome outcome,
        IReadOnlyList<JsonElement> signals,
        Func<string, CancellationToken, Task<PolicyDecision>> evaluateSkillPolicy,
        Func<InitiativeEvaluationContext, CancellationToken, Task<InitiativeEvaluationResult>> evaluateInitiative,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken ct)
    {
        var actionType = outcome.ActionType;

        if (string.IsNullOrWhiteSpace(actionType))
        {
            await EmitReflectionSkippedAsync(agentId, agentAddress, outcome, "UnknownActionType",
                "Outcome has no ActionType.", emitActivity, ct);
            return;
        }

        // Unit skill policy (#163 / C3) — a cross-cutting skill-allowlist
        // gate that applies to any skill invocation, not just initiative-driven
        // ones. Kept on the dispatch path because the initiative evaluator
        // does not own this concern. Passed as a delegate so the coordinator
        // can remain a singleton even though IUnitPolicyEnforcer is scoped.
        PolicyDecision unitDecision;
        try
        {
            unitDecision = await evaluateSkillPolicy(actionType, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Unit policy enforcer threw evaluating action {ActionType} for agent {AgentId}; allowing to avoid losing the action.",
                actionType, agentId);
            unitDecision = PolicyDecision.Allowed;
        }

        if (!unitDecision.IsAllowed)
        {
            await EmitReflectionSkippedAsync(agentId, agentAddress, outcome,
                "BlockedByUnitPolicy",
                unitDecision.Reason ?? $"Action '{actionType}' blocked by unit policy.",
                emitActivity, ct, unitId: unitDecision.DenyingUnitId);
            return;
        }

        var handler = reflectionActionHandlers.Find(actionType);
        if (handler is null)
        {
            await EmitReflectionSkippedAsync(agentId, agentAddress, outcome,
                "UnknownActionType",
                $"No handler registered for action type '{actionType}'.",
                emitActivity, ct);
            return;
        }

        Message? translated;
        try
        {
            translated = await handler.TranslateAsync(agentAddress, outcome, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Reflection action handler for {ActionType} threw for agent {AgentId}.",
                actionType, agentId);
            await EmitReflectionSkippedAsync(agentId, agentAddress, outcome,
                "HandlerThrew", ex.Message, emitActivity, ct);
            return;
        }

        if (translated is null)
        {
            await EmitReflectionSkippedAsync(agentId, agentAddress, outcome,
                "MalformedPayload",
                $"Handler for '{actionType}' rejected the payload.",
                emitActivity, ct);
            return;
        }

        // Initiative evaluator (#415 / PR #550). Composes the unit-scoped
        // initiative-action policy (#250), cost caps (#474), boundary /
        // hierarchy / cloning gates, and projects the result onto the
        // three-valued decision that drives Reactive / Proactive / Autonomous
        // semantics at runtime.
        var action = new InitiativeAction(
            ActionType: actionType,
            EstimatedCost: 0m,
            IsReversible: true,
            TargetAddress: $"{translated.To.Scheme}://{translated.To.Path}");

        InitiativeEvaluationResult evaluation;
        try
        {
            evaluation = await evaluateInitiative(
                new InitiativeEvaluationContext(agentId, action, signals),
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Evaluator infrastructure failure — fail closed to confirmation.
            logger.LogWarning(ex,
                "Initiative evaluator threw for agent {AgentId}, action {ActionType}; surfacing as confirmation-required proposal.",
                agentId, actionType);

            await EmitProposalAsync(agentAddress, outcome, translated,
                reason: $"initiative evaluator threw: {ex.Message}",
                effectiveLevel: null,
                failedClosed: true,
                emitActivity, ct);
            return;
        }

        switch (evaluation.Decision)
        {
            case InitiativeEvaluationDecision.Defer:
                // Issue #552: Defer takes no action and emits no activity
                // event. The internal log line keeps the decision traceable.
                logger.LogInformation(
                    "Reflection action '{ActionType}' deferred for agent {AgentId}: {Reason}",
                    actionType, agentId, evaluation.Reason ?? "(no reason)");
                return;

            case InitiativeEvaluationDecision.ActWithConfirmation:
                await EmitProposalAsync(agentAddress, outcome, translated,
                    reason: evaluation.Reason,
                    effectiveLevel: evaluation.EffectiveLevel,
                    failedClosed: evaluation.FailedClosed,
                    emitActivity, ct);
                return;

            case InitiativeEvaluationDecision.ActAutonomously:
                // Fall through to inline routing.
                break;

            default:
                logger.LogWarning(
                    "Initiative evaluator returned unknown decision {Decision} for agent {AgentId}; treating as Defer.",
                    evaluation.Decision, agentId);
                return;
        }

        var routing = await messageRouter.RouteAsync(translated, ct);
        if (!routing.IsSuccess)
        {
            logger.LogWarning(
                "Routing reflection action {ActionType} for agent {AgentId} failed: {Error}",
                actionType, agentId, routing.Error);
            await EmitReflectionSkippedAsync(agentId, agentAddress, outcome,
                "RoutingFailed",
                routing.Error?.Message ?? "router returned failure",
                emitActivity, ct);
            return;
        }

        var dispatchDetails = JsonSerializer.SerializeToElement(new
        {
            actionType,
            messageId = translated.Id,
            target = new { scheme = translated.To.Scheme, path = translated.To.Path },
            threadId = translated.ThreadId,
            effectiveLevel = evaluation.EffectiveLevel.ToString(),
        });

        await emitActivity(
            BuildEvent(
                agentAddress,
                ActivityEventType.ReflectionActionDispatched,
                ActivitySeverity.Info,
                $"Reflection action '{actionType}' dispatched to {translated.To.Scheme}://{translated.To.Path}.",
                details: dispatchDetails,
                correlationId: translated.ThreadId),
            ct);
    }

    private async Task EmitProposalAsync(
        Address agentAddress,
        ReflectionOutcome outcome,
        Message translated,
        string? reason,
        InitiativeLevel? effectiveLevel,
        bool failedClosed,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken ct)
    {
        var details = JsonSerializer.SerializeToElement(new
        {
            actionType = outcome.ActionType,
            messageId = translated.Id,
            target = new { scheme = translated.To.Scheme, path = translated.To.Path },
            threadId = translated.ThreadId,
            reason,
            effectiveLevel = effectiveLevel?.ToString(),
            failedClosed,
        });

        var summary = failedClosed
            ? $"Reflection action '{outcome.ActionType}' proposal (fail-closed): {reason ?? "(no reason)"}"
            : $"Reflection action '{outcome.ActionType}' proposal pending confirmation: {reason ?? "(no reason)"}";

        await emitActivity(
            BuildEvent(
                agentAddress,
                ActivityEventType.ReflectionActionProposed,
                ActivitySeverity.Info,
                summary,
                details: details,
                correlationId: translated.ThreadId),
            ct);
    }

    private async Task EmitReflectionSkippedAsync(
        string agentId,
        Address agentAddress,
        ReflectionOutcome outcome,
        string reason,
        string detail,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken ct,
        string? unitId = null)
    {
        logger.LogInformation(
            "Reflection action skipped for agent {AgentId}: {Reason} ({Detail})",
            agentId, reason, detail);

        var details = JsonSerializer.SerializeToElement(new
        {
            reason,
            detail,
            actionType = outcome.ActionType,
            denyingUnitId = unitId,
        });

        await emitActivity(
            BuildEvent(
                agentAddress,
                ActivityEventType.ReflectionActionSkipped,
                ActivitySeverity.Info,
                $"Reflection action skipped: {reason}",
                details: details),
            ct);
    }

    /// <summary>
    /// Produces a short, human-readable summary for an observation. If the observation is
    /// an object with a <c>summary</c> string property, that value is used. Otherwise the
    /// raw JSON is truncated to 200 characters.
    /// </summary>
    internal static string SummarizeObservation(JsonElement observation)
    {
        if (observation.ValueKind == JsonValueKind.Object &&
            observation.TryGetProperty("summary", out var summary) &&
            summary.ValueKind == JsonValueKind.String)
        {
            return summary.GetString() ?? observation.ToString();
        }

        var raw = observation.ToString();
        return raw.Length <= 200 ? raw : raw[..200];
    }

    private static ActivityEvent BuildEvent(
        Address source,
        ActivityEventType eventType,
        ActivitySeverity severity,
        string summary,
        JsonElement? details = null,
        string? correlationId = null)
    {
        return new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            source,
            eventType,
            severity,
            summary,
            details,
            correlationId);
    }
}