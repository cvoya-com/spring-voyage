// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Costs;

/// <summary>
/// One entry in a per-agent cost breakdown, keyed on model name or tool name
/// and annotated with the kind of cost it represents.
/// </summary>
/// <param name="Key">
/// The dimension value — a model name (e.g. <c>claude-3-5-sonnet</c>) when
/// <paramref name="Kind"/> is <c>"model"</c>, or a tool name when
/// <paramref name="Kind"/> is <c>"tool"</c>.
/// </param>
/// <param name="Kind">
/// The cost kind: <c>"model"</c> for LLM token cost rows or
/// <c>"tool"</c> for tool-invocation cost rows.
/// </param>
/// <param name="TotalCost">The summed cost (USD) across all records with this key.</param>
/// <param name="RecordCount">The number of individual cost records that contributed to <paramref name="TotalCost"/>.</param>
public record CostBreakdownEntry(
    string Key,
    string Kind,
    decimal TotalCost,
    int RecordCount);
