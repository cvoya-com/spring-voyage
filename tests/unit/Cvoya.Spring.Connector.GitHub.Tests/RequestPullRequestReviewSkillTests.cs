// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class RequestPullRequestReviewSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly RequestPullRequestReviewSkill _skill;

    public RequestPullRequestReviewSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new RequestPullRequestReviewSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_PassesReviewersAndReturnsUpdatedPr()
    {
        PullRequestReviewRequest? captured = null;
        _gitHubClient.PullRequest.ReviewRequest
            .Create("owner", "repo", 5, Arg.Do<PullRequestReviewRequest>(r => captured = r))
            .Returns(PrTestHelpers.CreatePullRequest(
                5,
                title: "Review me",
                htmlUrl: "https://github.com/owner/repo/pull/5",
                requestedReviewerLogins: ["alice", "bob"]));

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 5,
            reviewers: ["alice", "bob"],
            teamReviewers: [],
            TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.Reviewers.ShouldBe(new[] { "alice", "bob" });

        result.GetProperty("number").GetInt32().ShouldBe(5);
        result.GetProperty("requested_reviewers").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteAsync_NoReviewers_ThrowsArgumentException()
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            _skill.ExecuteAsync(
                "owner", "repo", 5,
                reviewers: [],
                teamReviewers: [],
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExecuteAsync_OctokitThrows_Propagates()
    {
        _gitHubClient.PullRequest.ReviewRequest
            .Create("owner", "repo", 5, Arg.Any<PullRequestReviewRequest>())
            .Returns<PullRequest>(_ =>
                throw new ApiValidationException());

        await Should.ThrowAsync<ApiValidationException>(() =>
            _skill.ExecuteAsync(
                "owner", "repo", 5,
                reviewers: ["alice"],
                teamReviewers: [],
                TestContext.Current.CancellationToken));
    }
}
