// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Units;

/// <summary>
/// Dashboard summary for an agent.
/// </summary>
/// <param name="Name">The agent's name (address path).</param>
/// <param name="DisplayName">The human-readable display name.</param>
/// <param name="Role">The agent's role, if any.</param>
/// <param name="RegisteredAt">When the agent was registered.</param>
public record AgentDashboardSummary(string Name, string DisplayName, string? Role, DateTimeOffset RegisteredAt);

/// <summary>
/// Dashboard summary for a unit.
/// </summary>
/// <param name="Name">The unit's name (address path).</param>
/// <param name="DisplayName">The human-readable display name.</param>
/// <param name="RegisteredAt">When the unit was registered.</param>
/// <param name="Status">The unit's current lifecycle status.</param>
public record UnitDashboardSummary(string Name, string DisplayName, DateTimeOffset RegisteredAt, UnitStatus Status);

/// <summary>
/// Dashboard summary for cost aggregation.
/// </summary>
/// <param name="TotalCost">The total cost across all sources.</param>
/// <param name="CostsBySource">Cost broken down by source.</param>
/// <param name="PeriodStart">The start of the reporting period.</param>
/// <param name="PeriodEnd">The end of the reporting period.</param>
public record CostDashboardSummary(decimal TotalCost, IReadOnlyList<CostBySource> CostsBySource, DateTimeOffset? PeriodStart, DateTimeOffset? PeriodEnd);