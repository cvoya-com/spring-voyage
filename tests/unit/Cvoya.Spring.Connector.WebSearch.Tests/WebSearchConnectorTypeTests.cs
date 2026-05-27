// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.WebSearch.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.WebSearch;
using Cvoya.Spring.Connectors;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

public class WebSearchConnectorTypeTests
{
    [Fact]
    public void TypeId_IsStable()
    {
        WebSearchConnectorType.WebSearchTypeId.ShouldBe(
            new Guid("f7c4d2e9-7b90-4f30-90e1-2b2df3a1c7a2"));
    }

    [Fact]
    public void BindingScope_IsUnit()
    {
        // ADR-0061 §1: only workspace-shaped connectors bind at the
        // tenant scope. Web-search stays per-unit.
        var sut = BuildConnector(Array.Empty<IWebSearchProvider>());
        sut.BindingScope.ShouldBe(BindingScope.Unit);
    }

    [Fact]
    public void BuildConfigSchema_EnumerateRegisteredProviders()
    {
        var providers = new IWebSearchProvider[]
        {
            StubProvider("brave", "Brave"),
            StubProvider("bing", "Bing"),
        };

        var schema = WebSearchConnectorType.BuildConfigSchema(providers);

        var providerProp = schema.GetProperty("properties").GetProperty("provider");
        providerProp.GetProperty("enum")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ShouldBe(new[] { "brave", "bing" });
    }

    [Fact]
    public void BuildConfigSchema_IncludesJsonSchemaMarker()
    {
        var schema = WebSearchConnectorType.BuildConfigSchema(new[] { StubProvider("brave", "Brave") });

        schema.TryGetProperty("$schema", out var marker).ShouldBeTrue();
        marker.GetString().ShouldBe("https://json-schema.org/draft/2020-12/schema");
    }

    [Fact]
    public void Describe_MentionsProviderPluggability()
    {
        var connectorType = BuildConnector(new[] { StubProvider("brave", "Brave") });

        connectorType.Description.ShouldContain("pluggable");
        connectorType.Slug.ShouldBe("web-search");
    }

    [Fact]
    public void ConfigRoundTrip_OmitsPlaintextSecret()
    {
        // The stored shape references a secret by name and never carries the
        // plaintext key. This test makes that guarantee explicit.
        var config = new UnitWebSearchConfig("brave", "brave-api-key", 10, true);
        var json = JsonSerializer.Serialize(config);
        json.ShouldNotContain("sk-");
        json.ShouldContain("brave-api-key");
    }

    private static IWebSearchProvider StubProvider(string id, string name)
    {
        var p = Substitute.For<IWebSearchProvider>();
        p.Id.Returns(id);
        p.DisplayName.Returns(name);
        return p;
    }

    private static WebSearchConnectorType BuildConnector(IWebSearchProvider[] providers)
    {
        var store = Substitute.For<IUnitConnectorConfigStore>();
        var options = Options.Create(new WebSearchConnectorOptions());
        return new WebSearchConnectorType(store, providers, options, NullLoggerFactory.Instance);
    }
}
