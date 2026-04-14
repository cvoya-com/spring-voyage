// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.GraphQL;

using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Connector.GitHub.GraphQL;

using NSubstitute;

using Shouldly;

using Xunit;

public class GraphQLBatchTests
{
    private sealed record Thing([property: JsonPropertyName("name")] string Name);

    [Fact]
    public void BuildQuery_ThreeAliases_ProducesOneCombinedQuery()
    {
        var batch = new GraphQLBatch();
        batch.Add<Thing>("alpha", "thing(id: 1) { name }");
        batch.Add<Thing>("beta", "thing(id: 2) { name }");
        batch.Add<Thing>("gamma", "thing(id: 3) { name }");

        var query = batch.BuildQuery();

        query.ShouldStartWith("query Batch {");
        query.ShouldContain("alpha: thing(id: 1) { name }");
        query.ShouldContain("beta: thing(id: 2) { name }");
        query.ShouldContain("gamma: thing(id: 3) { name }");
        batch.Count.ShouldBe(3);
    }

    [Fact]
    public void Add_DuplicateAlias_Throws()
    {
        var batch = new GraphQLBatch();
        batch.Add<Thing>("a", "x { y }");
        Should.Throw<ArgumentException>(() => batch.Add<Thing>("a", "x { y }"));
    }

    [Fact]
    public void Add_InvalidAlias_Throws()
    {
        var batch = new GraphQLBatch();
        Should.Throw<ArgumentException>(() => batch.Add<Thing>("1bad", "x { y }"));
        Should.Throw<ArgumentException>(() => batch.Add<Thing>("bad-name", "x { y }"));
    }

    [Fact]
    public void Add_ExceedsMax_Throws()
    {
        var batch = new GraphQLBatch(maxAliases: 2);
        batch.Add<Thing>("a", "x { y }");
        batch.Add<Thing>("b", "x { y }");
        Should.Throw<InvalidOperationException>(() => batch.Add<Thing>("c", "x { y }"));
    }

    [Fact]
    public async Task ExecuteAsync_SplitsResponseByAlias()
    {
        var batch = new GraphQLBatch();
        batch.Add<Thing>("one", "thing(id: 1) { name }");
        batch.Add<Thing>("two", "thing(id: 2) { name }");

        var responseData = JsonSerializer.Deserialize<JsonElement>(
            """{"one":{"name":"first"},"two":{"name":"second"}}""");

        var client = Substitute.For<IGitHubGraphQLClient>();
        client
            .QueryAsync<JsonElement>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(responseData));

        var result = await batch.ExecuteAsync(client, TestContext.Current.CancellationToken);

        result.Aliases.ShouldBe(new[] { "one", "two" }, ignoreOrder: true);
        result.Get<Thing>("one").Name.ShouldBe("first");
        result.Get<Thing>("two").Name.ShouldBe("second");
    }

    [Fact]
    public async Task ExecuteAsync_PartialFailure_SurfacesPerAliasError()
    {
        var batch = new GraphQLBatch();
        batch.Add<Thing>("ok", "x { y }");
        batch.Add<Thing>("missing", "x { y }");

        var responseData = JsonSerializer.Deserialize<JsonElement>(
            """{"ok":{"name":"here"}}""");

        var client = Substitute.For<IGitHubGraphQLClient>();
        client
            .QueryAsync<JsonElement>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(responseData));

        var result = await batch.ExecuteAsync(client, TestContext.Current.CancellationToken);

        result.Get<Thing>("ok").Name.ShouldBe("here");

        result.TryGet<Thing>("missing", out _, out var error).ShouldBeFalse();
        error.ShouldNotBeNull();
        error!.ShouldContain("missing");

        Should.Throw<GitHubGraphQLException>(() => result.Get<Thing>("missing"));
    }

    [Fact]
    public async Task ExecuteAsync_Empty_NoOp()
    {
        var batch = new GraphQLBatch();
        var client = Substitute.For<IGitHubGraphQLClient>();
        client
            .QueryAsync<JsonElement>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(JsonSerializer.Deserialize<JsonElement>("""{}""")));
        var result = await batch.ExecuteAsync(client, TestContext.Current.CancellationToken);
        result.Aliases.ShouldBeEmpty();
    }

    [Fact]
    public async Task ListReviewThreadsBatch_BatchesMultiplePullRequests()
    {
        var client = Substitute.For<IGitHubGraphQLClient>();
        string? capturedQuery = null;
        client
            .QueryAsync<JsonElement>(Arg.Do<string>(q => capturedQuery = q), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(JsonSerializer.Deserialize<JsonElement>(
                """
                {
                  "pr_0": {"pullRequest":{"reviewThreads":{"nodes":[{"id":"t1","isResolved":false,"isOutdated":false,"path":"a.cs","line":1,"comments":{"nodes":[]}}]}}},
                  "pr_1": {"pullRequest":{"reviewThreads":{"nodes":[]}}}
                }
                """)));

        var prs = new[]
        {
            new ListReviewThreadsBatch.PullRequestRef("o", "r", 1),
            new ListReviewThreadsBatch.PullRequestRef("o", "r", 2),
        };
        var results = await ListReviewThreadsBatch.ExecuteAsync(client, prs, TestContext.Current.CancellationToken);

        capturedQuery.ShouldNotBeNull();
        capturedQuery!.ShouldContain("pr_0: repository(owner: \"o\", name: \"r\")");
        capturedQuery.ShouldContain("pr_1: repository(owner: \"o\", name: \"r\")");

        results[prs[0]].Threads.Count.ShouldBe(1);
        results[prs[0]].Threads[0].Id.ShouldBe("t1");
        results[prs[1]].Threads.ShouldBeEmpty();
    }
}