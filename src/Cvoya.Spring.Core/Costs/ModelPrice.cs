// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Costs;

/// <summary>
/// The per-million-token USD price of a model, split by direction. Used by
/// <see cref="IModelPriceCatalog"/> to estimate the cost of a runtime turn
/// from the token counts a runtime reports on its <c>sv.llm.turn</c> span
/// when the runtime does not report a cost itself (the native / SV Agent SDK
/// path — issue #3075).
/// </summary>
/// <param name="InputPerMillionUsd">USD charged per 1,000,000 input (prompt) tokens.</param>
/// <param name="OutputPerMillionUsd">USD charged per 1,000,000 output (completion) tokens.</param>
public readonly record struct ModelPrice(
    decimal InputPerMillionUsd,
    decimal OutputPerMillionUsd)
{
    /// <summary>
    /// Estimates the USD cost of a turn that consumed
    /// <paramref name="inputTokens"/> input tokens and produced
    /// <paramref name="outputTokens"/> output tokens at this price.
    /// </summary>
    public decimal Estimate(long inputTokens, long outputTokens)
    {
        var input = Math.Max(0, inputTokens);
        var output = Math.Max(0, outputTokens);
        return ((input * InputPerMillionUsd) + (output * OutputPerMillionUsd)) / 1_000_000m;
    }
}
