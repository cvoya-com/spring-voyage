// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class EnsureIssueLinkedToPullRequestSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly EnsureIssueLinkedToPullRequestSkill _skill;

    public EnsureIssueLinkedToPullRequestSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new EnsureIssueLinkedToPullRequestSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_AppendsMissingClosesLines()
    {
        _gitHubClient.PullRequest.Get("owner", "repo", 5)
            .Returns(PrTestHelpers.CreatePullRequest(5, body: "Initial body.\n\nFixes #10"));
        PullRequestUpdate? captured = null;
        _gitHubClient.PullRequest
            .Update("owner", "repo", 5, Arg.Do<PullRequestUpdate>(u => captured = u))
            .Returns(PrTestHelpers.CreatePullRequest(5, body: "updated"));

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 5,
            issueNumbers: [10, 20],
            TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.Body!.ShouldContain("Closes #20");
        captured.Body!.ShouldContain("Fixes #10"); // original line preserved

        result.GetProperty("updated").GetBoolean().ShouldBeTrue();
        result.GetProperty("already_linked").GetArrayLength().ShouldBe(1);
        result.GetProperty("appended").GetArrayLength().ShouldBe(1);
        result.GetProperty("appended")[0].GetInt32().ShouldBe(20);
    }

    [Fact]
    public async Task ExecuteAsync_AllAlreadyLinked_NoUpdate()
    {
        _gitHubClient.PullRequest.Get("owner", "repo", 5)
            .Returns(PrTestHelpers.CreatePullRequest(5, body: "Closes #10 and resolves #20."));

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 5,
            issueNumbers: [10, 20],
            TestContext.Current.CancellationToken);

        await _gitHubClient.PullRequest.DidNotReceive()
            .Update("owner", "repo", 5, Arg.Any<PullRequestUpdate>());
        result.GetProperty("updated").GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_NoIssueNumbers_ThrowsArgumentException()
    {
        await Should.ThrowAsync<ArgumentException>(() =>
            _skill.ExecuteAsync(
                "owner", "repo", 5,
                issueNumbers: [],
                TestContext.Current.CancellationToken));
    }
}
