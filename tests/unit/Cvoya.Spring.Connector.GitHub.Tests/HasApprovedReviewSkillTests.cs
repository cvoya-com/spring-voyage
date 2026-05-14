// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class HasApprovedReviewSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly HasApprovedReviewSkill _skill;

    public HasApprovedReviewSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new HasApprovedReviewSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_ApprovedReview_ReturnsApprovedTrue()
    {
        _gitHubClient.PullRequest.Review.GetAll("owner", "repo", 1)
            .Returns(new[]
            {
                PrTestHelpers.CreateReview(1, PullRequestReviewState.Approved, reviewerLogin: "alice"),
            });

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 1, requiredReviewer: null,
            TestContext.Current.CancellationToken);

        result.GetProperty("approved").GetBoolean().ShouldBeTrue();
        result.GetProperty("approvers").GetArrayLength().ShouldBe(1);
        result.GetProperty("approvers")[0].GetString().ShouldBe("alice");
    }

    [Fact]
    public async Task ExecuteAsync_LatestReviewBeatsEarlierApproval()
    {
        // Alice first approves, then requests changes. Latest state wins → not approved.
        var now = DateTimeOffset.UtcNow;
        _gitHubClient.PullRequest.Review.GetAll("owner", "repo", 1)
            .Returns(new[]
            {
                PrTestHelpers.CreateReview(1, PullRequestReviewState.Approved,
                    reviewerLogin: "alice", submittedAt: now.AddMinutes(-10)),
                PrTestHelpers.CreateReview(2, PullRequestReviewState.ChangesRequested,
                    reviewerLogin: "alice", submittedAt: now),
            });

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 1, requiredReviewer: null,
            TestContext.Current.CancellationToken);

        result.GetProperty("approved").GetBoolean().ShouldBeFalse();
        result.GetProperty("approvers").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteAsync_RequiredReviewer_Missing_ReturnsFalse()
    {
        _gitHubClient.PullRequest.Review.GetAll("owner", "repo", 1)
            .Returns(new[]
            {
                PrTestHelpers.CreateReview(1, PullRequestReviewState.Approved, reviewerLogin: "alice"),
            });

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 1, requiredReviewer: "bob",
            TestContext.Current.CancellationToken);

        result.GetProperty("approved").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_NoReviews_ReturnsFalse()
    {
        _gitHubClient.PullRequest.Review.GetAll("owner", "repo", 1)
            .Returns(Array.Empty<PullRequestReview>());

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 1, requiredReviewer: null,
            TestContext.Current.CancellationToken);

        result.GetProperty("approved").GetBoolean().ShouldBeFalse();
        result.GetProperty("review_count").GetInt32().ShouldBe(0);
    }
}
