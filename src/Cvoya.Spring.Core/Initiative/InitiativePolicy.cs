// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

/// <summary>
/// Defines the initiative policy for a unit, controlling agent autonomy boundaries,
/// budget caps, and tiered cognition configuration.
/// </summary>
/// <param name="MaxLevel">The maximum initiative level agents in this unit may exercise.</param>
/// <param name="RequireUnitApproval">Whether agent-initiated actions require unit-level approval.</param>
/// <param name="Tier1">Configuration for the Tier 1 screening model.</param>
/// <param name="Tier2">Budget and rate limits for Tier 2 cognition.</param>
/// <param name="AllowedActions">Actions agents are permitted to take autonomously. Empty means all actions allowed.</param>
/// <param name="BlockedActions">Actions explicitly blocked regardless of initiative level.</param>
public record InitiativePolicy(
    InitiativeLevel MaxLevel = InitiativeLevel.Passive,
    bool RequireUnitApproval = false,
    Tier1Config? Tier1 = null,
    Tier2Config? Tier2 = null,
    IReadOnlyList<string>? AllowedActions = null,
    IReadOnlyList<string>? BlockedActions = null);

/// <summary>
/// Configuration for Tier 1 (screening) cognition — a small, cheap LLM used for fast event triage.
/// </summary>
/// <param name="Model">The model identifier for the Tier 1 screening LLM (e.g., "phi-3-mini").</param>
/// <param name="Hosting">Where the Tier 1 model runs (e.g., "platform" for shared infrastructure).</param>
public record Tier1Config(
    string Model = "phi-3-mini",
    string Hosting = "platform");

/// <summary>
/// Budget and rate limits for Tier 2 (primary LLM) cognition invocations.
/// </summary>
/// <param name="MaxCallsPerHour">Maximum number of Tier 2 invocations per hour.</param>
/// <param name="MaxCostPerDay">Maximum cost in dollars for Tier 2 invocations per day.</param>
public record Tier2Config(
    int MaxCallsPerHour = 5,
    decimal MaxCostPerDay = 3.00m);