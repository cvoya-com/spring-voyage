// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

using System.Text.Json;

/// <summary>
/// Context passed to <see cref="IAgentInitiativeEvaluator"/> describing the
/// agent, the signal(s) that triggered the evaluation, and the action the
/// agent is proposing to take. The evaluator consults the agent's live
/// policy on every call — callers do not need to attach the policy here.
/// </summary>
/// <param name="AgentId">The agent the evaluation is scoped to.</param>
/// <param name="Action">The proposed action, including its cost estimate.</param>
/// <param name="Signals">
/// The observed activity-stream signals that triggered this evaluation.
/// Empty when the evaluator is called from a direct-invocation path — in
/// that case <see cref="IAgentInitiativeEvaluator"/> returns
/// <see cref="InitiativeEvaluationDecision.Defer"/> for
/// <see cref="InitiativeLevel.Passive"/> / <see cref="InitiativeLevel.Attentive"/>
/// (Reactive baseline) because an initiative seam is only consulted on
/// autonomous-intent decisions.
/// </param>
public record InitiativeEvaluationContext(
    string AgentId,
    InitiativeAction Action,
    IReadOnlyList<JsonElement> Signals);
