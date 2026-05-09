// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class ListPullRequestsByUserSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly ListPullRequestsByUserSkill _skill;

    public ListPullRequestsByUserSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new ListPullRequestsByUserSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_Author_EmitsAuthorQualifier()
    {
        SearchIssuesRequest? captured = null;
        _gitHubClient.Search
            .SearchIssues(Arg.Do<SearchIssuesRequest>(r => captured = r))
            .Returns(PrTestHelpers.CreateSearchResult(
                totalCount: 1,
                items: new[]
                {
                    IssueTestHelpers.CreateIssue(
                        number: 7,
                        title: "from alice",
                        authorLogin: "alice",
                        htmlUrl: "https://github.com/owner/repo/pull/7"),
                }));

        var result = await _skill.ExecuteAsync(
            "owner", "repo", "alice",
            ListPullRequestsByUserSkill.UserRole.Author,
            state: "open", maxResults: 10,
            TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.Author.ShouldBe("alice");
        captured.Assignee.ShouldBeNullOrEmpty();
        captured.Is.ShouldContain(IssueIsQualifier.PullRequest);
        captured.State.ShouldBe(ItemState.Open);

        result.GetProperty("total_count").GetInt32().ShouldBe(1);
        result.GetProperty("pull_requests").GetArrayLength().ShouldBe(1);
        result.GetProperty("pull_requests")[0].GetProperty("author").GetString().ShouldBe("alice");
    }

    [Fact]
    public async Task ExecuteAsync_Assignee_EmitsAssigneeQualifier_AndHonorsAllState()
    {
        SearchIssuesRequest? captured = null;
        _gitHubClient.Search
            .SearchIssues(Arg.Do<SearchIssuesRequest>(r => captured = r))
            .Returns(PrTestHelpers.CreateSearchResult(0, Array.Empty<Issue>()));

        await _skill.ExecuteAsync(
            "owner", "repo", "bob",
            ListPullRequestsByUserSkill.UserRole.Assignee,
            state: "all", maxResults: 200,
            TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.Assignee.ShouldBe("bob");
        captured.Author.ShouldBeNullOrEmpty();
        captured.State.ShouldBeNull(); // "all" means no state qualifier.
        captured.PerPage.ShouldBe(100); // clamped
    }

    [Fact]
    public async Task ExecuteAsync_OctokitThrows_Propagates()
    {
        _gitHubClient.Search
            .SearchIssues(Arg.Any<SearchIssuesRequest>())
            .Returns<SearchIssuesResult>(_ => throw new ApiException("boom", System.Net.HttpStatusCode.InternalServerError));

        await Should.ThrowAsync<ApiException>(() =>
            _skill.ExecuteAsync(
                "owner", "repo", "alice",
                ListPullRequestsByUserSkill.UserRole.Author,
                state: null, maxResults: 10,
                TestContext.Current.CancellationToken));
    }
}
