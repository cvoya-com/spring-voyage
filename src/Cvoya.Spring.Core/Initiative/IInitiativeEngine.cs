// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

using System.Text.Json;

/// <summary>
/// Core engine coordinating the two-tier initiative model for an agent:
/// batches observation events, invokes Tier 1 screening and Tier 2 reflection via
/// <see cref="ICognitionProvider"/> instances, and enforces the configured
/// <see cref="InitiativePolicy"/> (including action allow/block lists and budget caps).
/// </summary>
public interface IInitiativeEngine
{
    /// <summary>
    /// Process a batch of observation events for an agent. Each event is first screened
    /// (Tier 1); events that screen as <see cref="InitiativeDecision.QueueForReflection"/>
    /// or <see cref="InitiativeDecision.ActImmediately"/> flow into Tier 2 reflection,
    /// subject to the agent's policy budget caps.
    /// </summary>
    /// <param name="agentId">The agent whose observations are being processed.</param>
    /// <param name="observations">The observation events to evaluate.</param>
    /// <param name="agentInstructions">
    /// The agent's configured instructions / system prompt sourced from the agent's
    /// definition. Plumbed through to the screening and reflection contexts so the
    /// cognition providers reason against the agent's real role description rather
    /// than a synthesised stand-in. <c>null</c> or empty when the agent has no
    /// instructions configured; the engine substitutes a small documented fallback
    /// in that case.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The reflection outcome, if Tier 2 was invoked; otherwise <c>null</c>.</returns>
    Task<ReflectionOutcome?> ProcessObservationsAsync(
        string agentId,
        IReadOnlyList<JsonElement> observations,
        string? agentInstructions,
        CancellationToken cancellationToken);

    /// <summary>
    /// Get the current initiative level for an agent, taking into account both the
    /// agent's own configuration and the enclosing unit's policy ceiling.
    /// </summary>
    /// <param name="agentId">The agent to query.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The effective initiative level.</returns>
    Task<InitiativeLevel> GetCurrentLevelAsync(string agentId, CancellationToken cancellationToken);

    /// <summary>
    /// Update the initiative policy for a target (agent or unit). The target kind is
    /// encoded into <paramref name="targetId"/> by the caller (e.g., the API layer).
    /// </summary>
    /// <param name="targetId">The agent or unit identifier.</param>
    /// <param name="policy">The new policy.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task SetPolicyAsync(string targetId, InitiativePolicy policy, CancellationToken cancellationToken);
}