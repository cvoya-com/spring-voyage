// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class FindPullRequestForBranchSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly FindPullRequestForBranchSkill _skill;

    public FindPullRequestForBranchSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new FindPullRequestForBranchSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_FiltersOnHead_UsingOwnerPrefix()
    {
        PullRequestRequest? capturedFilter = null;
        _gitHubClient.PullRequest
            .GetAllForRepository(
                "owner", "repo",
                Arg.Do<PullRequestRequest>(f => capturedFilter = f),
                Arg.Any<ApiOptions>())
            .Returns(new[]
            {
                PrTestHelpers.CreatePullRequest(42, title: "Feature", htmlUrl: "https://github.com/owner/repo/pull/42"),
            });

        var result = await _skill.ExecuteAsync(
            "owner", "repo", "feature-x", headOwner: null, includeClosed: false,
            TestContext.Current.CancellationToken);

        capturedFilter.ShouldNotBeNull();
        capturedFilter!.Head.ShouldBe("owner:feature-x");
        capturedFilter.State.ShouldBe(ItemStateFilter.Open);

        result.GetProperty("found").GetBoolean().ShouldBeTrue();
        result.GetProperty("pull_request").GetProperty("number").GetInt32().ShouldBe(42);
    }

    [Fact]
    public async Task ExecuteAsync_HeadOwnerOverride_UsesForkOwner()
    {
        PullRequestRequest? capturedFilter = null;
        _gitHubClient.PullRequest
            .GetAllForRepository(
                "owner", "repo",
                Arg.Do<PullRequestRequest>(f => capturedFilter = f),
                Arg.Any<ApiOptions>())
            .Returns(Array.Empty<PullRequest>());

        var result = await _skill.ExecuteAsync(
            "owner", "repo", "patch", headOwner: "contributor", includeClosed: true,
            TestContext.Current.CancellationToken);

        capturedFilter.ShouldNotBeNull();
        capturedFilter!.Head.ShouldBe("contributor:patch");
        capturedFilter.State.ShouldBe(ItemStateFilter.All);

        result.GetProperty("found").GetBoolean().ShouldBeFalse();
    }
}
