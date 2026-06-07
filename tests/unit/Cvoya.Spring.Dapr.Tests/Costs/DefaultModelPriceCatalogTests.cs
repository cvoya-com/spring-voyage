// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Costs;

using Cvoya.Spring.Dapr.Costs;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="DefaultModelPriceCatalog"/> — the OSS default
/// model→price table the native / SV Agent SDK cost path uses to estimate a
/// turn's cost from its model and token counts (#3075).
/// </summary>
public class DefaultModelPriceCatalogTests
{
    private readonly DefaultModelPriceCatalog _catalog = new();

    [Fact]
    public void EstimateCostUsd_KnownModelExactId_ComputesFromTokens()
    {
        // claude-opus-4-8 → claude-opus-4 family: 15 in / 75 out per million.
        // 1,000,000 in + 1,000,000 out = 15 + 75 = 90.
        var cost = _catalog.EstimateCostUsd("claude-opus-4-8", 1_000_000, 1_000_000);

        cost.ShouldBe(90m);
    }

    [Fact]
    public void EstimateCostUsd_DatedAlias_ResolvesToFamilyPrefix()
    {
        // A dated alias must resolve to its family price via longest-prefix.
        var dated = _catalog.EstimateCostUsd("claude-opus-4-8-20260101", 1_000_000, 0);
        var family = _catalog.EstimateCostUsd("claude-opus-4-8", 1_000_000, 0);

        dated.ShouldBe(15m);
        dated.ShouldBe(family);
    }

    [Fact]
    public void EstimateCostUsd_LongestPrefixWins_OverShorterFamily()
    {
        // gpt-4o-mini is far cheaper than gpt-4o; the longer prefix must win
        // so a mini turn is not priced at the gpt-4o rate.
        var mini = _catalog.EstimateCostUsd("gpt-4o-mini", 1_000_000, 1_000_000);
        var full = _catalog.EstimateCostUsd("gpt-4o", 1_000_000, 1_000_000);

        mini.ShouldBe(0.75m); // 0.15 + 0.60
        full.ShouldBe(12.50m); // 2.50 + 10
        mini!.Value.ShouldBeLessThan(full!.Value);
    }

    [Fact]
    public void EstimateCostUsd_CaseInsensitive()
    {
        var lower = _catalog.EstimateCostUsd("claude-sonnet-4-6", 500_000, 200_000);
        var upper = _catalog.EstimateCostUsd("CLAUDE-SONNET-4-6", 500_000, 200_000);

        lower.ShouldNotBeNull();
        upper.ShouldBe(lower);
    }

    [Fact]
    public void EstimateCostUsd_UnknownModel_ReturnsNull()
    {
        _catalog.EstimateCostUsd("some-unlisted-model-9000", 1_000_000, 1_000_000)
            .ShouldBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EstimateCostUsd_NullOrBlankModel_ReturnsNull(string? model)
    {
        _catalog.EstimateCostUsd(model, 1_000_000, 1_000_000).ShouldBeNull();
    }

    [Fact]
    public void EstimateCostUsd_ZeroTokens_ReturnsNull()
    {
        // A known model with no tokens has no cost — never emit a $0 record.
        _catalog.EstimateCostUsd("claude-opus-4-8", 0, 0).ShouldBeNull();
    }

    [Fact]
    public void EstimateCostUsd_NegativeTokens_ClampedToZero()
    {
        // Defensive: a negative token count (corrupt span) must not produce a
        // negative cost; the input side clamps to zero so only the output side
        // bills.
        var cost = _catalog.EstimateCostUsd("claude-opus-4-8", -100, 1_000_000);

        cost.ShouldBe(75m);
    }
}
