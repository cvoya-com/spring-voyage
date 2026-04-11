// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

using System.Text.Json;

/// <summary>
/// The result of Tier 2 reflection — the agent's decided action (or inaction).
/// </summary>
/// <param name="ShouldAct">Whether the agent decided to take an action.</param>
/// <param name="ActionType">The type of action to take, if any (e.g., "send-message", "start-conversation").</param>
/// <param name="Reasoning">The agent's reasoning for its decision.</param>
/// <param name="ActionPayload">Structured data for executing the decided action, if any.</param>
public record ReflectionOutcome(
    bool ShouldAct,
    string? ActionType = null,
    string? Reasoning = null,
    JsonElement? ActionPayload = null);