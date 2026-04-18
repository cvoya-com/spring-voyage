// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Arxiv.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.Arxiv;
using Cvoya.Spring.Connector.Arxiv.Skills;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

public class ArxivSkillRegistryTests
{
    [Fact]
    public void Exposes_SearchLiterature_And_FetchAbstract()
    {
        var registry = BuildRegistry(Substitute.For<IArxivClient>());

        var tools = registry.GetToolDefinitions();

        tools.Select(t => t.Name).ShouldBe(new[] { "searchLiterature", "fetchAbstract" });
        registry.Name.ShouldBe("arxiv");
    }

    [Fact]
    public async Task InvokeAsync_SearchLiterature_DispatchesToClient()
    {
        var client = Substitute.For<IArxivClient>();
        client.SearchAsync(
                Arg.Any<string>(), Arg.Any<IReadOnlyList<string>?>(),
                Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ArxivEntry(
                    "2401.00001", "Title", "Summary",
                    new[] { "A. Author" }, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
                    "cs.AI", new[] { "cs.AI" }, "pdf", "abs", null),
            });

        var registry = BuildRegistry(client);
        var args = JsonSerializer.SerializeToElement(new
        {
            query = "scaling laws",
            categories = new[] { "cs.AI" },
            limit = 5,
        });

        var result = await registry.InvokeAsync("searchLiterature", args, TestContext.Current.CancellationToken);

        result.GetProperty("count").GetInt32().ShouldBe(1);
        var first = result.GetProperty("results")[0];
        first.GetProperty("id").GetString().ShouldBe("2401.00001");
    }

    [Fact]
    public async Task InvokeAsync_FetchAbstract_ReturnsFoundFalseWhenMissing()
    {
        var client = Substitute.For<IArxivClient>();
        client.GetByIdAsync("1234.5678", Arg.Any<CancellationToken>())
            .Returns((ArxivEntry?)null);

        var registry = BuildRegistry(client);
        var args = JsonSerializer.SerializeToElement(new { arxivId = "1234.5678" });

        var result = await registry.InvokeAsync("fetchAbstract", args, TestContext.Current.CancellationToken);

        result.GetProperty("found").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task InvokeAsync_UnknownTool_Throws()
    {
        var registry = BuildRegistry(Substitute.For<IArxivClient>());

        await Should.ThrowAsync<SkillNotFoundException>(async () =>
            await registry.InvokeAsync("nope", default, TestContext.Current.CancellationToken));
    }

    private static ArxivSkillRegistry BuildRegistry(IArxivClient client)
    {
        var search = new SearchLiteratureSkill(client, NullLoggerFactory.Instance);
        var fetch = new FetchAbstractSkill(client, NullLoggerFactory.Instance);
        return new ArxivSkillRegistry(search, fetch, NullLoggerFactory.Instance);
    }
}