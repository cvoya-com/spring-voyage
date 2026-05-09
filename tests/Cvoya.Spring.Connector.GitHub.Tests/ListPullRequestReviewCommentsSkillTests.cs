// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class ListPullRequestReviewCommentsSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly ListPullRequestReviewCommentsSkill _skill;

    public ListPullRequestReviewCommentsSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new ListPullRequestReviewCommentsSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsProjectedLineComments()
    {
        ApiOptions? capturedOptions = null;
        _gitHubClient.PullRequest.ReviewComment
            .GetAll("owner", "repo", 5, Arg.Do<ApiOptions>(o => capturedOptions = o))
            .Returns(new[]
            {
                PrTestHelpers.CreateReviewComment(1, "nit", "src/a.cs", position: 10, authorLogin: "alice"),
                PrTestHelpers.CreateReviewComment(2, "blocker", "src/b.cs", position: 20, authorLogin: "bob"),
            });

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 5, maxResults: 25,
            TestContext.Current.CancellationToken);

        capturedOptions.ShouldNotBeNull();
        capturedOptions!.PageSize.ShouldBe(25);

        result.GetProperty("count").GetInt32().ShouldBe(2);
        result.GetProperty("review_comments")[0].GetProperty("path").GetString().ShouldBe("src/a.cs");
        result.GetProperty("review_comments")[0].GetProperty("position").GetInt32().ShouldBe(10);
    }

    [Fact]
    public async Task ExecuteAsync_Empty_ReturnsCountZero()
    {
        _gitHubClient.PullRequest.ReviewComment
            .GetAll("owner", "repo", 5, Arg.Any<ApiOptions>())
            .Returns(Array.Empty<PullRequestReviewComment>());

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 5, maxResults: 30,
            TestContext.Current.CancellationToken);

        result.GetProperty("count").GetInt32().ShouldBe(0);
    }
}
