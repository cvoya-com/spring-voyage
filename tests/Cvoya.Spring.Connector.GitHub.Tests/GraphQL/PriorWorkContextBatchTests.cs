// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.GraphQL;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.GraphQL;

using NSubstitute;

using Shouldly;

using Xunit;

public class PriorWorkContextBatchTests
{
    private static IGitHubGraphQLClient MockClient(string responseJson, Action<string>? captureQuery = null)
    {
        var client = Substitute.For<IGitHubGraphQLClient>();
        client
            .QueryAsync<JsonElement>(
                Arg.Do<string>(q => captureQuery?.Invoke(q)),
                Arg.Any<object?>(),
                Arg.Any<CancellationToken>())
            .Returns(JsonSerializer.Deserialize<JsonElement>(responseJson));
        return client;
    }

    [Fact]
    public async Task ExecuteAsync_SingleBatchedCall_OneGraphqlQuotaDecrement()
    {
        var responseJson = """
            {
              "mentions_search":  { "nodes": [] },
              "authored_search":  { "nodes": [] },
              "commented_search": { "nodes": [] },
              "assigned_search":  { "nodes": [] }
            }
            """;
        var client = MockClient(responseJson);

        await PriorWorkContextBatch.ExecuteAsync(
            client,
            owner: "o",
            repo: "r",
            user: "bot",
            since: null,
            perBucket: 20,
            TestContext.Current.CancellationToken);

        // One GraphQL round-trip means one `graphql` quota decrement
        // (vs four `search`-bucket decrements before D12).
        await client.Received(1).QueryAsync<JsonElement>(
            Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_AllAliasesPresent_PopulatesEveryBucket()
    {
        var responseJson = """
            {
              "mentions_search":  { "nodes": [ { "__typename": "Issue", "number": 1, "title": "m1", "url": "u1", "state": "OPEN" } ] },
              "authored_search":  { "nodes": [ { "__typename": "PullRequest", "number": 2, "title": "a1", "url": "u2", "state": "OPEN" } ] },
              "commented_search": { "nodes": [ { "__typename": "Issue", "number": 3, "title": "c1", "url": "u3", "state": "CLOSED" } ] },
              "assigned_search":  { "nodes": [ { "__typename": "Issue", "number": 4, "title": "a2", "url": "u4", "state": "OPEN" } ] }
            }
            """;
        var client = MockClient(responseJson);

        var result = await PriorWorkContextBatch.ExecuteAsync(
            client, "o", "r", "bot", since: null, perBucket: 20,
            TestContext.Current.CancellationToken);

        result.Mentions.Items.Count.ShouldBe(1);
        result.Mentions.Items[0].Type.ShouldBe("issue");
        result.Authored.Items.Count.ShouldBe(1);
        result.Authored.Items[0].Type.ShouldBe("pull_request");
        result.Commented.Items.Count.ShouldBe(1);
        result.Assigned.Items.Count.ShouldBe(1);
        result.Mentions.Error.ShouldBeNull();
        result.Authored.Error.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_PartialFailure_SurfacesPerBucketErrorWithoutPoisoningOthers()
    {
        // The assigned bucket is missing entirely — simulates an aliased
        // sub-query that GitHub omitted from the response.
        var responseJson = """
            {
              "mentions_search":  { "nodes": [] },
              "authored_search":  { "nodes": [] },
              "commented_search": { "nodes": [] }
            }
            """;
        var client = MockClient(responseJson);

        var result = await PriorWorkContextBatch.ExecuteAsync(
            client, "o", "r", "bot", since: null, perBucket: 20,
            TestContext.Current.CancellationToken);

        result.Assigned.Error.ShouldNotBeNullOrWhiteSpace();
        result.Assigned.Items.ShouldBeEmpty();

        // Other buckets come back clean.
        result.Mentions.Error.ShouldBeNull();
        result.Authored.Error.ShouldBeNull();
        result.Commented.Error.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_SinceFilter_AppendsUpdatedQualifier()
    {
        string? capturedQuery = null;
        var client = MockClient("""
            {
              "mentions_search":  { "nodes": [] },
              "authored_search":  { "nodes": [] },
              "commented_search": { "nodes": [] },
              "assigned_search":  { "nodes": [] }
            }
            """, q => capturedQuery = q);

        var since = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero);
        await PriorWorkContextBatch.ExecuteAsync(
            client, "o", "r", "bot", since, perBucket: 20,
            TestContext.Current.CancellationToken);

        capturedQuery.ShouldNotBeNull();
        capturedQuery!.ShouldContain("updated:>2025-03-01T00:00:00Z");
    }

    [Fact]
    public async Task ExecuteAsync_InvalidInputs_Throw()
    {
        var client = MockClient("{}");

        await Should.ThrowAsync<ArgumentException>(() => PriorWorkContextBatch.ExecuteAsync(
            client, owner: "", repo: "r", user: "u", since: null, perBucket: 5,
            TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() => PriorWorkContextBatch.ExecuteAsync(
            client, owner: "o", repo: "r", user: "u", since: null, perBucket: 0,
            TestContext.Current.CancellationToken));
    }
}