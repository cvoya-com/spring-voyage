// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class GetPriorWorkContextSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly GetPriorWorkContextSkill _skill;

    public GetPriorWorkContextSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new GetPriorWorkContextSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsBucketedSummaryShape()
    {
        // Each bucket query returns one distinct item so we can verify
        // routing by looking at titles downstream.
        _gitHubClient.Search
            .SearchIssues(Arg.Any<SearchIssuesRequest>())
            .Returns(
                ci => PrTestHelpers.CreateSearchResult(1, new[]
                {
                    IssueTestHelpers.CreateIssue(
                        number: 7,
                        title: "sample",
                        body: "body",
                        htmlUrl: "https://github.com/owner/repo/issues/7",
                        authorLogin: "bot-user"),
                }));

        var result = await _skill.ExecuteAsync(
            "owner", "repo", "bot-user",
            since: null,
            maxPerBucket: 5,
            TestContext.Current.CancellationToken);

        // Four queries (mentions, authored, commented, assigned).
        await _gitHubClient.Search.Received(4).SearchIssues(Arg.Any<SearchIssuesRequest>());

        result.GetProperty("user").GetString().ShouldBe("bot-user");
        result.GetProperty("repository").GetProperty("full_name").GetString().ShouldBe("owner/repo");
        result.GetProperty("mentions").GetProperty("count").GetInt32().ShouldBe(1);
        result.GetProperty("authored_pull_requests").GetProperty("count").GetInt32().ShouldBe(1);
        result.GetProperty("commented_issues").GetProperty("count").GetInt32().ShouldBe(1);
        result.GetProperty("assigned_issues").GetProperty("count").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_IssuesDistinctQualifiersPerBucket()
    {
        var captured = new List<string>();
        _gitHubClient.Search
            .SearchIssues(Arg.Do<SearchIssuesRequest>(r => captured.Add(r.Term)))
            .Returns(PrTestHelpers.CreateSearchResult(0, Array.Empty<Issue>()));

        await _skill.ExecuteAsync(
            "owner", "repo", "bot-user",
            since: null,
            maxPerBucket: 5,
            TestContext.Current.CancellationToken);

        captured.Count.ShouldBe(4);
        captured.ShouldContain(t => t.Contains("mentions:bot-user"));
        captured.ShouldContain(t => t.Contains("author:bot-user") && t.Contains("is:pr"));
        captured.ShouldContain(t => t.Contains("commenter:bot-user") && t.Contains("is:issue"));
        captured.ShouldContain(t => t.Contains("assignee:bot-user") && t.Contains("is:issue"));
    }

    [Fact]
    public async Task ExecuteAsync_ClampsMaxPerBucket()
    {
        var captured = new List<int>();
        _gitHubClient.Search
            .SearchIssues(Arg.Do<SearchIssuesRequest>(r => captured.Add(r.PerPage)))
            .Returns(PrTestHelpers.CreateSearchResult(0, Array.Empty<Issue>()));

        await _skill.ExecuteAsync(
            "owner", "repo", "bot-user",
            since: null,
            maxPerBucket: 10_000,
            TestContext.Current.CancellationToken);

        captured.ShouldAllBe(v => v == 100);
    }
}