// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class CloseIssueSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly CloseIssueSkill _skill;

    public CloseIssueSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new CloseIssueSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_NoReason_SetsStateToClosed()
    {
        IssueUpdate? captured = null;
        _gitHubClient.Issue
            .Update("owner", "repo", 11L, Arg.Do<IssueUpdate>(u => captured = u))
            .Returns(IssueTestHelpers.CreateIssue(number: 11, state: ItemState.Closed));

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 11, reason: null,
            TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.State.ShouldBe(ItemState.Closed);
        captured.StateReason.ShouldBeNull();

        result.GetProperty("number").GetInt32().ShouldBe(11);
        result.GetProperty("state").GetString().ShouldBe("closed");
    }

    [Theory]
    [InlineData("completed", ItemStateReason.Completed)]
    [InlineData("not_planned", ItemStateReason.NotPlanned)]
    [InlineData("NOT-PLANNED", ItemStateReason.NotPlanned)]
    [InlineData("reopened", ItemStateReason.Reopened)]
    public async Task ExecuteAsync_WithReason_SetsStateReason(string input, ItemStateReason expected)
    {
        IssueUpdate? captured = null;
        _gitHubClient.Issue
            .Update("owner", "repo", 12L, Arg.Do<IssueUpdate>(u => captured = u))
            .Returns(IssueTestHelpers.CreateIssue(number: 12, state: ItemState.Closed));

        await _skill.ExecuteAsync(
            "owner", "repo", 12, reason: input,
            TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.StateReason.ShouldBe(expected);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownReason_LeavesStateReasonUnset()
    {
        IssueUpdate? captured = null;
        _gitHubClient.Issue
            .Update("owner", "repo", 13L, Arg.Do<IssueUpdate>(u => captured = u))
            .Returns(IssueTestHelpers.CreateIssue(number: 13, state: ItemState.Closed));

        await _skill.ExecuteAsync(
            "owner", "repo", 13, reason: "pizza",
            TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.StateReason.ShouldBeNull();
    }
}