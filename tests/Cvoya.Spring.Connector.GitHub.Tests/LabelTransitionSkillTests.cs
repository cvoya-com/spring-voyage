// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Labels;
using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class LabelTransitionSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly LabelStateMachine _stateMachine;
    private readonly LabelTransitionSkill _skill;

    public LabelTransitionSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _stateMachine = new LabelStateMachine(LabelStateMachineOptions.Default());
        _skill = new LabelTransitionSkill(_gitHubClient, _stateMachine, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_LegalTransition_RemovesOldAndAddsNewLabel()
    {
        _gitHubClient.Issue.Labels
            .GetAllForIssue("owner", "repo", 42)
            .Returns(new[]
            {
                IssueTestHelpers.CreateLabel("needs-triage"),
                IssueTestHelpers.CreateLabel("bug"),
            });

        _gitHubClient.Issue.Labels
            .AddToIssue("owner", "repo", 42, Arg.Any<string[]>())
            .Returns(new[]
            {
                IssueTestHelpers.CreateLabel("in-progress"),
                IssueTestHelpers.CreateLabel("bug"),
            });

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 42, "in-progress",
            TestContext.Current.CancellationToken);

        result.GetProperty("transitioned").GetBoolean().ShouldBeTrue();
        result.GetProperty("from").GetString().ShouldBe("needs-triage");
        result.GetProperty("to").GetString().ShouldBe("in-progress");

        await _gitHubClient.Issue.Labels.Received(1)
            .RemoveFromIssue("owner", "repo", 42, "needs-triage");
        await _gitHubClient.Issue.Labels.Received(1)
            .AddToIssue("owner", "repo", 42, Arg.Is<string[]>(s => s.Length == 1 && s[0] == "in-progress"));
    }

    [Fact]
    public async Task ExecuteAsync_IllegalTransition_ThrowsWithValidTransitions()
    {
        _gitHubClient.Issue.Labels
            .GetAllForIssue("owner", "repo", 42)
            .Returns(new[] { IssueTestHelpers.CreateLabel("resolved") });

        var ex = await Should.ThrowAsync<InvalidLabelTransitionException>(() =>
            _skill.ExecuteAsync(
                "owner", "repo", 42, "in-progress",
                TestContext.Current.CancellationToken));

        ex.From.ShouldBe("resolved");
        ex.To.ShouldBe("in-progress");
        ex.ValidTransitionsFromCurrent.Count.ShouldBe(0);
        ex.Message.ShouldContain("valid transitions from current", Case.Insensitive);
    }

    [Fact]
    public async Task ExecuteAsync_AlreadyInTargetState_IsNoop()
    {
        _gitHubClient.Issue.Labels
            .GetAllForIssue("owner", "repo", 42)
            .Returns(new[] { IssueTestHelpers.CreateLabel("in-progress") });

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 42, "in-progress",
            TestContext.Current.CancellationToken);

        result.GetProperty("transitioned").GetBoolean().ShouldBeFalse();
        result.GetProperty("reason").GetString().ShouldBe("already_in_target_state");

        await _gitHubClient.Issue.Labels.DidNotReceiveWithAnyArgs()
            .AddToIssue(default!, default!, default, default!);
        await _gitHubClient.Issue.Labels.DidNotReceiveWithAnyArgs()
            .RemoveFromIssue(default!, default!, default, default!);
    }

    [Fact]
    public async Task ExecuteAsync_UnknownToState_Throws()
    {
        var ex = await Should.ThrowAsync<ArgumentException>(() =>
            _skill.ExecuteAsync(
                "owner", "repo", 42, "not-a-state",
                TestContext.Current.CancellationToken));

        ex.Message.ShouldContain("not a configured state label", Case.Insensitive);
    }

    [Fact]
    public async Task ExecuteAsync_FromBlankSlate_AllowsInitialState()
    {
        _gitHubClient.Issue.Labels
            .GetAllForIssue("owner", "repo", 42)
            .Returns(Array.Empty<Label>());

        _gitHubClient.Issue.Labels
            .AddToIssue("owner", "repo", 42, Arg.Any<string[]>())
            .Returns(new[] { IssueTestHelpers.CreateLabel("needs-triage") });

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 42, "needs-triage",
            TestContext.Current.CancellationToken);

        result.GetProperty("transitioned").GetBoolean().ShouldBeTrue();
        result.GetProperty("to").GetString().ShouldBe("needs-triage");
    }
}