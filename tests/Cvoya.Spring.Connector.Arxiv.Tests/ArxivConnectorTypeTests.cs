// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Arxiv.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.Arxiv;

using Shouldly;

using Xunit;

public class ArxivConnectorTypeTests
{
    [Fact]
    public void Slug_IsStable()
    {
        // Changing these would invalidate existing bindings — a deliberate
        // drift guard so accidental renames get caught in review.
        ArxivConnectorType.ArxivTypeId.ShouldBe(
            new Guid("b3c2f5a1-1d38-4a56-8c18-9ac8b2b2d401"));
    }

    [Fact]
    public void BuildConfigSchema_ExposesDefaultCategoriesAndMaxResults()
    {
        var element = ArxivConnectorType.BuildConfigSchema();

        var props = element.GetProperty("properties");
        props.TryGetProperty("defaultCategories", out _).ShouldBeTrue();
        props.TryGetProperty("maxResults", out var max).ShouldBeTrue();
        max.GetProperty("maximum").GetInt32().ShouldBe(100);
        max.GetProperty("minimum").GetInt32().ShouldBe(1);
    }

    [Fact]
    public void ConfigRoundTrip_PreservesShape()
    {
        var config = new UnitArxivConfig(new[] { "cs.AI", "cs.LG" }, 15);
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var roundTrip = JsonSerializer.Deserialize<UnitArxivConfig>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        roundTrip.ShouldNotBeNull();
        roundTrip!.DefaultCategories.ShouldNotBeNull();
        roundTrip.DefaultCategories!.Count.ShouldBe(2);
        roundTrip.MaxResults.ShouldBe(15);
    }
}