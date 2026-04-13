// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class AssignIssueSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly AssignIssueSkill _skill;

    public AssignIssueSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new AssignIssueSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_AddsAndRemovesAssignees_ReturnsUpdatedList()
    {
        AssigneesUpdate? capturedAdd = null;
        AssigneesUpdate? capturedRemove = null;

        _gitHubClient.Issue.Assignee
            .AddAssignees("owner", "repo", 42L, Arg.Do<AssigneesUpdate>(u => capturedAdd = u))
            .Returns(IssueTestHelpers.CreateIssue(number: 42));

        _gitHubClient.Issue.Assignee
            .RemoveAssignees("owner", "repo", 42L, Arg.Do<AssigneesUpdate>(u => capturedRemove = u))
            .Returns(IssueTestHelpers.CreateIssue(number: 42, assigneeLogins: ["alice"]));

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 42,
            assigneesToAdd: ["alice"],
            assigneesToRemove: ["bob"],
            TestContext.Current.CancellationToken);

        capturedAdd.ShouldNotBeNull();
        capturedAdd!.Assignees.ShouldContain("alice");
        capturedRemove.ShouldNotBeNull();
        capturedRemove!.Assignees.ShouldContain("bob");

        result.GetProperty("number").GetInt32().ShouldBe(42);
        result.GetProperty("assignees").GetArrayLength().ShouldBe(1);
        result.GetProperty("assignees")[0].GetString().ShouldBe("alice");
    }

    [Fact]
    public async Task ExecuteAsync_OnlyAdd_DoesNotCallRemove()
    {
        _gitHubClient.Issue.Assignee
            .AddAssignees("owner", "repo", 43L, Arg.Any<AssigneesUpdate>())
            .Returns(IssueTestHelpers.CreateIssue(number: 43, assigneeLogins: ["alice"]));

        _gitHubClient.Issue.Get("owner", "repo", 43L)
            .Returns(IssueTestHelpers.CreateIssue(number: 43, assigneeLogins: ["alice"]));

        await _skill.ExecuteAsync(
            "owner", "repo", 43,
            assigneesToAdd: ["alice"],
            assigneesToRemove: [],
            TestContext.Current.CancellationToken);

        await _gitHubClient.Issue.Assignee.DidNotReceive()
            .RemoveAssignees(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<AssigneesUpdate>());
    }

    [Fact]
    public async Task ExecuteAsync_OnlyRemove_DoesNotCallAdd()
    {
        _gitHubClient.Issue.Assignee
            .RemoveAssignees("owner", "repo", 44L, Arg.Any<AssigneesUpdate>())
            .Returns(IssueTestHelpers.CreateIssue(number: 44));

        await _skill.ExecuteAsync(
            "owner", "repo", 44,
            assigneesToAdd: [],
            assigneesToRemove: ["bob"],
            TestContext.Current.CancellationToken);

        await _gitHubClient.Issue.Assignee.DidNotReceive()
            .AddAssignees(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<AssigneesUpdate>());
    }
}