// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Costs;

/// <summary>
/// Estimates the USD cost of a runtime turn from its model id and token
/// counts. Used for runtimes that report tokens but not a cost — the native
/// / SV Agent SDK path, whose <c>sv.llm.turn</c> OTLP span carries
/// <c>llm.tokens.input/output</c> and <c>llm.model</c> but no cost (the gap
/// #3075 closes). The Claude Code path reports its own
/// <c>total_cost_usd</c> and does not go through this catalog.
/// </summary>
/// <remarks>
/// <para>
/// The interface lives in <c>Cvoya.Spring.Core</c> with no infrastructure
/// dependency so a cloud overlay can register a richer catalogue — one
/// backed by a live price feed, per-tenant negotiated rates, or a wider
/// model set — without taking a dependency on the Dapr layer. The OSS
/// default (<c>DefaultModelPriceCatalog</c>) ships a static table of public
/// list prices for the models the bundled runtimes use.
/// </para>
/// <para>
/// A catalogue returns <c>null</c> for a model it has no price for. Callers
/// treat an unpriced turn the same as a zero-cost turn — no
/// <c>CostIncurred</c> activity is emitted — so an unknown model never
/// fabricates a spend figure.
/// </para>
/// </remarks>
public interface IModelPriceCatalog
{
    /// <summary>
    /// Estimates the cost in USD of a turn against <paramref name="model"/>
    /// that consumed <paramref name="inputTokens"/> input tokens and
    /// produced <paramref name="outputTokens"/> output tokens.
    /// </summary>
    /// <param name="model">
    /// The model id the turn billed against (e.g. the <c>llm.model</c> span
    /// attribute). Matching is implementation-defined; the OSS default does a
    /// case-insensitive longest-prefix match so a dated alias
    /// (<c>claude-opus-4-8-20260101</c>) resolves to its family price.
    /// </param>
    /// <param name="inputTokens">Input (prompt) tokens consumed this turn.</param>
    /// <param name="outputTokens">Output (completion) tokens generated this turn.</param>
    /// <returns>
    /// The estimated cost in USD, or <c>null</c> when the catalogue has no
    /// price for <paramref name="model"/> (the caller then emits no cost).
    /// </returns>
    decimal? EstimateCostUsd(string? model, long inputTokens, long outputTokens);
}
