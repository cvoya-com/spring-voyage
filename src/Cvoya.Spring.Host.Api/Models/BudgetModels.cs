// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Request body for setting an agent or tenant cost budget.
/// </summary>
/// <param name="DailyBudget">The daily cost budget in USD.</param>
public record SetBudgetRequest(decimal DailyBudget);

/// <summary>
/// Response body representing the current cost budget.
/// </summary>
/// <param name="DailyBudget">The daily cost budget in USD.</param>
public record BudgetResponse(decimal DailyBudget);