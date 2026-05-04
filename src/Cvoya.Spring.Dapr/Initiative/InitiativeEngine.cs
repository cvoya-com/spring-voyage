// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Initiative;

using System.Text.Json;

using Cvoya.Spring.Core.Initiative;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Orchestrates the two-tier initiative model for an agent: screens observations
/// via Tier 1, escalates to Tier 2 reflection when warranted, and enforces
/// <see cref="InitiativePolicy"/> (max level, allowed/blocked actions, budget caps).
/// </summary>
public class InitiativeEngine : IInitiativeEngine
{
    /// <summary>
    /// Number of queued observations that trigger Tier 2 reflection when none of the
    /// observations screen as <see cref="InitiativeDecision.ActImmediately"/>.
    /// </summary>
    public const int QueueReflectionThreshold = 3;

    /// <summary>
    /// Estimated cost (in dollars) charged against the agent's budget before each
    /// Tier 2 invocation. A refined post-hoc charge can be added once real token
    /// accounting is wired in.
    /// </summary>
    public const decimal Tier2EstimatedCost = 0.10m;

    /// <summary>
    /// Documented fallback used for the screening / reflection contexts when the
    /// agent has no <c>Instructions</c> configured on its definition. Distinct
    /// from any historical placeholder so an operator inspecting prompts can
    /// tell that the engine is falling back rather than that the agent has a
    /// degenerate one-line "Allowed actions:" prompt.
    /// </summary>
    internal const string MissingInstructionsFallback =
        "(no agent instructions configured)";

    private readonly ICognitionProvider _tier1;
    private readonly ICognitionProvider _tier2;
    private readonly IAgentPolicyStore _policyStore;
    private readonly IInitiativeBudgetTracker _budgetTracker;
    private readonly ILogger<InitiativeEngine> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="InitiativeEngine"/> class.
    /// </summary>
    /// <param name="tier1">The Tier 1 (screening) cognition provider.</param>
    /// <param name="tier2">The Tier 2 (reflection) cognition provider.</param>
    /// <param name="policyStore">Store used to load and persist agent/unit initiative policy.</param>
    /// <param name="budgetTracker">Tracker enforcing per-agent Tier 2 budget caps.</param>
    /// <param name="logger">Logger.</param>
    public InitiativeEngine(
        [FromKeyedServices("tier1")] ICognitionProvider tier1,
        [FromKeyedServices("tier2")] ICognitionProvider tier2,
        IAgentPolicyStore policyStore,
        IInitiativeBudgetTracker budgetTracker,
        ILogger<InitiativeEngine> logger)
    {
        _tier1 = tier1;
        _tier2 = tier2;
        _policyStore = policyStore;
        _budgetTracker = budgetTracker;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ReflectionOutcome?> ProcessObservationsAsync(
        string agentId,
        IReadOnlyList<JsonElement> observations,
        string? agentInstructions,
        CancellationToken cancellationToken)
    {
        var policy = await _policyStore.GetPolicyAsync(agentId, cancellationToken);

        if (policy.MaxLevel == InitiativeLevel.Passive)
        {
            _logger.LogDebug("Initiative disabled (MaxLevel=Passive) for agent {AgentId}; skipping.", agentId);
            return null;
        }

        if (observations.Count == 0)
        {
            return null;
        }

        var resolvedInstructions = string.IsNullOrWhiteSpace(agentInstructions)
            ? MissingInstructionsFallback
            : agentInstructions;
        var allowedActions = policy.AllowedActions ?? Array.Empty<string>();

        var queued = new List<JsonElement>(observations.Count);
        var actImmediately = false;

        foreach (var observation in observations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var screeningContext = new ScreeningContext(
                AgentId: agentId,
                AgentInstructions: resolvedInstructions,
                InitiativeLevel: policy.MaxLevel,
                EventSummary: SummarizeObservation(observation),
                EventPayload: observation);

            InitiativeDecision decision;
            try
            {
                decision = await _tier1.ScreenAsync(screeningContext, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Tier 1 screening threw for agent {AgentId}; treating observation as QueueForReflection.",
                    agentId);
                decision = InitiativeDecision.QueueForReflection;
            }

            switch (decision)
            {
                case InitiativeDecision.Ignore:
                    break;
                case InitiativeDecision.QueueForReflection:
                    queued.Add(observation);
                    break;
                case InitiativeDecision.ActImmediately:
                    queued.Add(observation);
                    actImmediately = true;
                    break;
            }
        }

        var shouldReflect = actImmediately || queued.Count >= QueueReflectionThreshold;
        if (!shouldReflect || queued.Count == 0)
        {
            return null;
        }

        var consumed = await _budgetTracker.TryConsumeAsync(agentId, Tier2EstimatedCost, cancellationToken);
        if (!consumed)
        {
            _logger.LogWarning("initiative budget exhausted for agent {AgentId}", agentId);
            return null;
        }

        var reflectionContext = new ReflectionContext(
            AgentId: agentId,
            AgentInstructions: resolvedInstructions,
            InitiativeLevel: policy.MaxLevel,
            Observations: queued,
            AllowedActions: allowedActions);

        ReflectionOutcome outcome;
        try
        {
            outcome = await _tier2.ReflectAsync(reflectionContext, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Tier 2 reflection threw for agent {AgentId}; returning non-acting outcome.",
                agentId);
            return new ReflectionOutcome(
                ShouldAct: false,
                ActionType: null,
                Reasoning: $"Tier 2 reflection failed: {ex.Message}",
                ActionPayload: null);
        }

        return ApplyPolicyToOutcome(outcome, policy);
    }

    /// <inheritdoc />
    public Task<InitiativeLevel> GetCurrentLevelAsync(string agentId, CancellationToken cancellationToken)
    {
        return _policyStore.GetEffectiveLevelAsync(agentId, cancellationToken);
    }

    /// <inheritdoc />
    public Task SetPolicyAsync(string targetId, InitiativePolicy policy, CancellationToken cancellationToken)
    {
        return _policyStore.SetPolicyAsync(targetId, policy, cancellationToken);
    }

    private static ReflectionOutcome ApplyPolicyToOutcome(ReflectionOutcome outcome, InitiativePolicy policy)
    {
        if (!outcome.ShouldAct || outcome.ActionType is null)
        {
            return outcome;
        }

        var actionType = outcome.ActionType;

        var blocked = policy.BlockedActions;
        if (blocked is not null && blocked.Contains(actionType))
        {
            return new ReflectionOutcome(
                ShouldAct: false,
                ActionType: actionType,
                Reasoning: $"action blocked by policy: {actionType}",
                ActionPayload: outcome.ActionPayload);
        }

        var allowed = policy.AllowedActions;
        if (allowed is { Count: > 0 } && !allowed.Contains(actionType))
        {
            return new ReflectionOutcome(
                ShouldAct: false,
                ActionType: actionType,
                Reasoning: $"action blocked by policy: {actionType}",
                ActionPayload: outcome.ActionPayload);
        }

        return outcome;
    }

    private static string SummarizeObservation(JsonElement observation)
    {
        if (observation.ValueKind == JsonValueKind.Object &&
            observation.TryGetProperty("summary", out var summary) &&
            summary.ValueKind == JsonValueKind.String)
        {
            return summary.GetString() ?? "(observation)";
        }

        var raw = observation.GetRawText();
        return raw.Length <= 160 ? raw : raw[..160] + "...";
    }
}