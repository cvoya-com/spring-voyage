// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.GraphQL;
using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class EnableAutoMergeSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly IGitHubGraphQLClient _graphQLClient;
    private readonly EnableAutoMergeSkill _skill;

    public EnableAutoMergeSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        _graphQLClient = Substitute.For<IGitHubGraphQLClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new EnableAutoMergeSkill(_gitHubClient, _graphQLClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_PostsGraphQlMutation_ReturnsEnabled()
    {
        _gitHubClient.PullRequest.Get("owner", "repo", 42)
            .Returns(PrTestHelpers.CreatePullRequest(42, nodeId: "PR_node123"));

        string? capturedMutation = null;
        object? capturedVariables = null;

        _graphQLClient
            .MutateAsync<JsonElement>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                capturedMutation = call.ArgAt<string>(0);
                capturedVariables = call.ArgAt<object?>(1);
                return Task.FromResult(JsonSerializer.Deserialize<JsonElement>("""{"ok":true}"""));
            });

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 42, mergeMethod: "squash",
            commitHeadline: "Headline", commitBody: "Body",
            TestContext.Current.CancellationToken);

        capturedMutation.ShouldNotBeNull();
        capturedMutation!.ShouldContain("enablePullRequestAutoMerge");

        var vars = (Dictionary<string, object?>)capturedVariables!;
        vars["prId"].ShouldBe("PR_node123");
        vars["mergeMethod"].ShouldBe("SQUASH");
        vars["headline"].ShouldBe("Headline");
        vars["body"].ShouldBe("Body");

        result.GetProperty("enabled").GetBoolean().ShouldBeTrue();
        result.GetProperty("node_id").GetString().ShouldBe("PR_node123");
        result.GetProperty("merge_method").GetString().ShouldBe("SQUASH");
    }

    [Fact]
    public async Task ExecuteAsync_GraphQlErrors_PropagatesException()
    {
        _gitHubClient.PullRequest.Get("owner", "repo", 42)
            .Returns(PrTestHelpers.CreatePullRequest(42, nodeId: "PR_node123"));

        _graphQLClient
            .MutateAsync<JsonElement>(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<CancellationToken>())
            .Returns<Task<JsonElement>>(_ =>
                throw new GitHubGraphQLException(["auto merge not enabled for this repo"]));

        var ex = await Should.ThrowAsync<GitHubGraphQLException>(() =>
            _skill.ExecuteAsync(
                "owner", "repo", 42, mergeMethod: null, commitHeadline: null, commitBody: null,
                TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("auto merge not enabled for this repo");
    }

    [Fact]
    public async Task ExecuteAsync_MissingNodeId_Throws()
    {
        _gitHubClient.PullRequest.Get("owner", "repo", 42)
            .Returns(PrTestHelpers.CreatePullRequest(42, nodeId: null));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            _skill.ExecuteAsync(
                "owner", "repo", 42, mergeMethod: null, commitHeadline: null, commitBody: null,
                TestContext.Current.CancellationToken));
    }
}