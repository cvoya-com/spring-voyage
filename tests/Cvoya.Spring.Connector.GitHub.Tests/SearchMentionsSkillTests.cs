// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class SearchMentionsSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly SearchMentionsSkill _skill;

    public SearchMentionsSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new SearchMentionsSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_BuildsMentionsQueryForGivenUser()
    {
        SearchIssuesRequest? capturedRequest = null;
        _gitHubClient.Search
            .SearchIssues(Arg.Do<SearchIssuesRequest>(r => capturedRequest = r))
            .Returns(PrTestHelpers.CreateSearchResult(0, Array.Empty<Issue>()));

        await _skill.ExecuteAsync(
            "owner", "repo", "bot-user",
            since: null,
            maxResults: 25,
            TestContext.Current.CancellationToken);

        capturedRequest.ShouldNotBeNull();
        capturedRequest!.Term.ShouldContain("mentions:bot-user");
        capturedRequest.PerPage.ShouldBe(25);
    }

    [Fact]
    public async Task ExecuteAsync_StripsLeadingAt()
    {
        SearchIssuesRequest? capturedRequest = null;
        _gitHubClient.Search
            .SearchIssues(Arg.Do<SearchIssuesRequest>(r => capturedRequest = r))
            .Returns(PrTestHelpers.CreateSearchResult(0, Array.Empty<Issue>()));

        await _skill.ExecuteAsync(
            "owner", "repo", "@bot-user",
            since: null,
            maxResults: 10,
            TestContext.Current.CancellationToken);

        capturedRequest!.Term.ShouldContain("mentions:bot-user");
        capturedRequest.Term.ShouldNotContain("@bot-user");
    }

    [Fact]
    public async Task ExecuteAsync_ClampsMaxResults()
    {
        SearchIssuesRequest? capturedRequest = null;
        _gitHubClient.Search
            .SearchIssues(Arg.Do<SearchIssuesRequest>(r => capturedRequest = r))
            .Returns(PrTestHelpers.CreateSearchResult(0, Array.Empty<Issue>()));

        await _skill.ExecuteAsync(
            "owner", "repo", "bot-user",
            since: null,
            maxResults: 10_000,
            TestContext.Current.CancellationToken);

        capturedRequest!.PerPage.ShouldBe(100);
    }

    [Fact]
    public async Task ExecuteAsync_ProjectsIssuesAndPullRequests()
    {
        var issueA = IssueTestHelpers.CreateIssue(
            number: 1,
            title: "Please @bot-user help",
            body: "Hey @bot-user, can you take a look?",
            htmlUrl: "https://github.com/owner/repo/issues/1",
            authorLogin: "requester");
        var issueB = IssueTestHelpers.CreateIssue(
            number: 2,
            title: "PR mentions bot",
            body: "cc @bot-user please review",
            htmlUrl: "https://github.com/owner/repo/pull/2",
            authorLogin: "other");

        _gitHubClient.Search
            .SearchIssues(Arg.Any<SearchIssuesRequest>())
            .Returns(PrTestHelpers.CreateSearchResult(2, new[] { issueA, issueB }));

        var result = await _skill.ExecuteAsync(
            "owner", "repo", "bot-user",
            since: null,
            maxResults: 30,
            TestContext.Current.CancellationToken);

        result.GetProperty("count").GetInt32().ShouldBe(2);
        result.GetProperty("total_count").GetInt32().ShouldBe(2);
        var mentions = result.GetProperty("mentions");
        mentions[0].GetProperty("number").GetInt32().ShouldBe(1);
        mentions[0].GetProperty("excerpt").GetString()!.ShouldContain("@bot-user");
    }

    [Fact]
    public async Task ExecuteAsync_WithSince_AttachesDateFilter()
    {
        SearchIssuesRequest? capturedRequest = null;
        _gitHubClient.Search
            .SearchIssues(Arg.Do<SearchIssuesRequest>(r => capturedRequest = r))
            .Returns(PrTestHelpers.CreateSearchResult(0, Array.Empty<Issue>()));

        var since = new DateTimeOffset(2026, 4, 1, 0, 0, 0, TimeSpan.Zero);

        await _skill.ExecuteAsync(
            "owner", "repo", "bot-user",
            since: since,
            maxResults: 10,
            TestContext.Current.CancellationToken);

        capturedRequest!.Updated.ShouldNotBeNull();
    }
}