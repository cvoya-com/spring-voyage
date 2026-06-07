// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Costs;

using Cvoya.Spring.Core.Costs;

/// <summary>
/// OSS default <see cref="IModelPriceCatalog"/>: a static table of public
/// list prices (USD per million tokens) for the model families the bundled
/// agent runtimes use. Applied to the native / SV Agent SDK cost path, whose
/// <c>sv.llm.turn</c> span reports tokens but no cost (#3075).
/// </summary>
/// <remarks>
/// <para>
/// Matching is case-insensitive longest-prefix: a dated alias such as
/// <c>claude-opus-4-8-20260101</c> resolves to the <c>claude-opus-4-8</c>
/// family price. An unknown model returns <c>null</c> so the caller emits no
/// cost rather than fabricating a figure against a price it does not have.
/// </para>
/// <para>
/// Prices are list prices as of the table's authoring and will drift; this
/// catalogue is an estimate for the OSS deployment. A cloud overlay
/// registers its own <see cref="IModelPriceCatalog"/> (live feed, negotiated
/// rates, wider model set) via <c>TryAdd</c> ahead of <c>AddCvoyaSpringDapr</c>
/// to override it. Registering the table here — not in <c>Cvoya.Spring.Core</c>
/// — keeps the dependency-free Core contract implementation-agnostic.
/// </para>
/// </remarks>
public sealed class DefaultModelPriceCatalog : IModelPriceCatalog
{
    // Public list prices in USD per 1,000,000 tokens (input, output). Keys
    // are lowercase family prefixes; lookup is longest-prefix so a dated or
    // suffixed alias falls back to its family. Ordered longest-first at
    // query time, so a more specific prefix wins over a shorter one.
    private static readonly IReadOnlyDictionary<string, ModelPrice> Prices =
        new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
        {
            // Anthropic Claude.
            ["claude-opus-4"] = new(InputPerMillionUsd: 15m, OutputPerMillionUsd: 75m),
            ["claude-sonnet-4"] = new(InputPerMillionUsd: 3m, OutputPerMillionUsd: 15m),
            ["claude-haiku-4"] = new(InputPerMillionUsd: 1m, OutputPerMillionUsd: 5m),
            ["claude-3-5-sonnet"] = new(InputPerMillionUsd: 3m, OutputPerMillionUsd: 15m),
            ["claude-3-5-haiku"] = new(InputPerMillionUsd: 0.80m, OutputPerMillionUsd: 4m),
            ["claude-3-opus"] = new(InputPerMillionUsd: 15m, OutputPerMillionUsd: 75m),

            // OpenAI GPT.
            ["gpt-4o-mini"] = new(InputPerMillionUsd: 0.15m, OutputPerMillionUsd: 0.60m),
            ["gpt-4o"] = new(InputPerMillionUsd: 2.50m, OutputPerMillionUsd: 10m),

            // Google Gemini.
            ["gemini-1.5-pro"] = new(InputPerMillionUsd: 1.25m, OutputPerMillionUsd: 5m),
            ["gemini-1.5-flash"] = new(InputPerMillionUsd: 0.075m, OutputPerMillionUsd: 0.30m),
        };

    private static readonly IReadOnlyList<string> PrefixesLongestFirst =
        Prices.Keys.OrderByDescending(k => k.Length).ToArray();

    /// <inheritdoc />
    public decimal? EstimateCostUsd(string? model, long inputTokens, long outputTokens)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var price = MatchPrice(model);
        if (price is not { } p)
        {
            return null;
        }

        var cost = p.Estimate(inputTokens, outputTokens);
        return cost > 0m ? cost : null;
    }

    private static ModelPrice? MatchPrice(string model)
    {
        // Exact match wins outright; otherwise the longest family prefix the
        // model id starts with.
        if (Prices.TryGetValue(model, out var exact))
        {
            return exact;
        }

        foreach (var prefix in PrefixesLongestFirst)
        {
            if (model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return Prices[prefix];
            }
        }

        return null;
    }
}
