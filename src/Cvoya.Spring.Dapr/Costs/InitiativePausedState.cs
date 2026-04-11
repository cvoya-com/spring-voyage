// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Costs;

/// <summary>
/// Represents a paused initiative state, written by the <see cref="BudgetEnforcer"/>
/// when an agent exceeds its cost budget.
/// </summary>
/// <param name="Reason">The reason the initiative was paused.</param>
/// <param name="PausedAt">The timestamp when the initiative was paused.</param>
public record InitiativePausedState(string Reason, DateTimeOffset PausedAt);