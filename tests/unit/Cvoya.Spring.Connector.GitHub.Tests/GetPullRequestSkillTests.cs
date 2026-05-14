// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class GetPullRequestSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly GetPullRequestSkill _skill;

    public GetPullRequestSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new GetPullRequestSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsProjectedDetail()
    {
        _gitHubClient.PullRequest.Get("owner", "repo", 17)
            .Returns(PrTestHelpers.CreatePullRequest(
                number: 17,
                title: "Add feature",
                body: "Details",
                htmlUrl: "https://github.com/owner/repo/pull/17",
                authorLogin: "alice",
                headRef: "feature",
                headSha: "deadbeef",
                baseRef: "main",
                labels: ["enhancement"],
                assigneeLogins: ["bob"],
                draft: false));

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 17,
            TestContext.Current.CancellationToken);

        result.GetProperty("number").GetInt32().ShouldBe(17);
        result.GetProperty("title").GetString().ShouldBe("Add feature");
        result.GetProperty("body").GetString().ShouldBe("Details");
        result.GetProperty("head").GetString().ShouldBe("feature");
        result.GetProperty("head_sha").GetString().ShouldBe("deadbeef");
        result.GetProperty("base").GetString().ShouldBe("main");
        result.GetProperty("author").GetString().ShouldBe("alice");
        result.GetProperty("labels").GetArrayLength().ShouldBe(1);
        result.GetProperty("assignees").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_NotFound_Propagates()
    {
        _gitHubClient.PullRequest.Get("owner", "repo", 99)
            .Returns<PullRequest>(_ => throw new NotFoundException("nope", System.Net.HttpStatusCode.NotFound));

        await Should.ThrowAsync<NotFoundException>(() =>
            _skill.ExecuteAsync("owner", "repo", 99, TestContext.Current.CancellationToken));
    }
}
