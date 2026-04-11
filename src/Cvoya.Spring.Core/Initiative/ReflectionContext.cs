// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

using System.Text.Json;

/// <summary>
/// Context provided to Tier 2 reflection for the full cognition loop.
/// </summary>
/// <param name="AgentId">The identifier of the agent performing reflection.</param>
/// <param name="AgentInstructions">The agent's configured instructions and expertise description.</param>
/// <param name="InitiativeLevel">The maximum initiative level permitted for this agent.</param>
/// <param name="Observations">Batched observation events accumulated since the last reflection.</param>
/// <param name="AllowedActions">The set of action identifiers the agent is permitted to take.</param>
public record ReflectionContext(
    string AgentId,
    string AgentInstructions,
    InitiativeLevel InitiativeLevel,
    IReadOnlyList<JsonElement> Observations,
    IReadOnlyList<string> AllowedActions);