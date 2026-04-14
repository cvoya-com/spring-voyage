// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class CreateIssueSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly CreateIssueSkill _skill;

    public CreateIssueSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new CreateIssueSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_PassesTitleBodyLabelsAssignees_ToOctokit()
    {
        NewIssue? captured = null;
        _gitHubClient.Issue
            .Create("owner", "repo", Arg.Do<NewIssue>(n => captured = n))
            .Returns(IssueTestHelpers.CreateIssue(
                number: 7,
                title: "New bug",
                htmlUrl: "https://github.com/owner/repo/issues/7",
                labelNames: ["bug", "priority:p1"],
                assigneeLogins: ["alice"]));

        var result = await _skill.ExecuteAsync(
            "owner", "repo", "New bug", "Repro steps",
            ["bug", "priority:p1"], ["alice"],
            TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.Title.ShouldBe("New bug");
        captured.Body.ShouldBe("Repro steps");
        captured.Labels.ShouldBe(new[] { "bug", "priority:p1" });
        captured.Assignees.ShouldBe(new[] { "alice" });

        result.GetProperty("number").GetInt32().ShouldBe(7);
        result.GetProperty("title").GetString().ShouldBe("New bug");
        result.GetProperty("html_url").GetString().ShouldBe("https://github.com/owner/repo/issues/7");
        result.GetProperty("labels").GetArrayLength().ShouldBe(2);
        result.GetProperty("assignees").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task ExecuteAsync_OmitsBodyWhenEmpty()
    {
        NewIssue? captured = null;
        _gitHubClient.Issue
            .Create("owner", "repo", Arg.Do<NewIssue>(n => captured = n))
            .Returns(IssueTestHelpers.CreateIssue(number: 8, title: "No body"));

        await _skill.ExecuteAsync(
            "owner", "repo", "No body", null, [], [],
            TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.Body.ShouldBeNull();
    }
}