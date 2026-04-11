// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Observability;

/// <summary>
/// Aggregated cost for a single source.
/// </summary>
/// <param name="Source">The event source identifier.</param>
/// <param name="TotalCost">The total cost for this source.</param>
public record CostBySource(string Source, decimal TotalCost);