// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class ListPullRequestReviewsSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly ListPullRequestReviewsSkill _skill;

    public ListPullRequestReviewsSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new ListPullRequestReviewsSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_ProjectsReviews()
    {
        _gitHubClient.PullRequest.Review.GetAll("owner", "repo", 5)
            .Returns(new[]
            {
                PrTestHelpers.CreateReview(1, PullRequestReviewState.Approved, reviewerLogin: "alice"),
                PrTestHelpers.CreateReview(2, PullRequestReviewState.ChangesRequested, reviewerLogin: "bob"),
            });

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 5,
            TestContext.Current.CancellationToken);

        result.GetProperty("count").GetInt32().ShouldBe(2);
        var reviews = result.GetProperty("reviews");
        reviews[0].GetProperty("reviewer").GetString().ShouldBe("alice");
        reviews[0].GetProperty("state").GetString().ShouldBe("APPROVED");
        reviews[1].GetProperty("state").GetString().ShouldBe("CHANGES_REQUESTED");
    }

    [Fact]
    public async Task ExecuteAsync_NotFound_Propagates()
    {
        _gitHubClient.PullRequest.Review.GetAll("owner", "repo", 99)
            .Returns<IReadOnlyList<PullRequestReview>>(_ =>
                throw new NotFoundException("no pr", System.Net.HttpStatusCode.NotFound));

        await Should.ThrowAsync<NotFoundException>(() =>
            _skill.ExecuteAsync("owner", "repo", 99, TestContext.Current.CancellationToken));
    }
}
