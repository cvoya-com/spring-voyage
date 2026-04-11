// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

using System.Text.Json;

/// <summary>
/// Context provided to Tier 1 screening for fast event evaluation.
/// </summary>
/// <param name="AgentId">The identifier of the agent being evaluated.</param>
/// <param name="AgentInstructions">The agent's configured instructions and expertise description.</param>
/// <param name="InitiativeLevel">The maximum initiative level permitted for this agent.</param>
/// <param name="EventSummary">A concise summary of the event to screen.</param>
/// <param name="EventPayload">The structured event payload, if available.</param>
public record ScreeningContext(
    string AgentId,
    string AgentInstructions,
    InitiativeLevel InitiativeLevel,
    string EventSummary,
    JsonElement? EventPayload = null);