// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.WebSearch.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.WebSearch.Providers;

using Shouldly;

using Xunit;

public class BraveSearchProviderParserTests
{
    [Fact]
    public void ParseResults_ExtractsTitleUrlSnippetAndSource()
    {
        const string body = """
        {
          "web": {
            "results": [
              { "title": "A", "url": "https://a.example", "description": "first", "profile": { "name": "ExampleSite" } },
              { "title": "B", "url": "https://b.example", "description": "second" }
            ]
          }
        }
        """;
        using var doc = JsonDocument.Parse(body);

        var results = BraveSearchProvider.ParseResults(doc);

        results.Count.ShouldBe(2);
        results[0].Title.ShouldBe("A");
        results[0].Url.ShouldBe("https://a.example");
        results[0].Snippet.ShouldBe("first");
        results[0].Source.ShouldBe("ExampleSite");
        results[1].Source.ShouldBeNull();
    }

    [Fact]
    public void ParseResults_EmptyWhenWebBlockMissing()
    {
        using var doc = JsonDocument.Parse("""{"mixed": {}}""");

        var results = BraveSearchProvider.ParseResults(doc);

        results.Count.ShouldBe(0);
    }
}
