// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class ListIssuesSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly ListIssuesSkill _skill;

    public ListIssuesSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new ListIssuesSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_BuildsFilterFromArguments_AndReturnsIssues()
    {
        RepositoryIssueRequest? capturedFilter = null;
        ApiOptions? capturedOptions = null;

        _gitHubClient.Issue
            .GetAllForRepository(
                "owner",
                "repo",
                Arg.Do<RepositoryIssueRequest>(f => capturedFilter = f),
                Arg.Do<ApiOptions>(o => capturedOptions = o))
            .Returns(new[]
            {
                IssueTestHelpers.CreateIssue(number: 1, title: "Bug A", authorLogin: "alice"),
                IssueTestHelpers.CreateIssue(number: 2, title: "Bug B", authorLogin: "bob"),
            });

        var result = await _skill.ExecuteAsync(
            "owner", "repo",
            state: "closed",
            labels: ["bug"],
            assignee: "alice",
            maxResults: 50,
            TestContext.Current.CancellationToken);

        capturedFilter.ShouldNotBeNull();
        capturedFilter!.State.ShouldBe(ItemStateFilter.Closed);
        capturedFilter.Labels.ShouldContain("bug");
        capturedFilter.Assignee.ShouldBe("alice");

        capturedOptions.ShouldNotBeNull();
        capturedOptions!.PageSize.ShouldBe(50);

        result.GetProperty("count").GetInt32().ShouldBe(2);
        result.GetProperty("issues").GetArrayLength().ShouldBe(2);
        result.GetProperty("issues")[0].GetProperty("author").GetString().ShouldBe("alice");
    }

    [Fact]
    public async Task ExecuteAsync_DefaultState_IsOpen()
    {
        RepositoryIssueRequest? capturedFilter = null;

        _gitHubClient.Issue
            .GetAllForRepository(
                "owner",
                "repo",
                Arg.Do<RepositoryIssueRequest>(f => capturedFilter = f),
                Arg.Any<ApiOptions>())
            .Returns(Array.Empty<Issue>());

        await _skill.ExecuteAsync(
            "owner", "repo",
            state: null,
            labels: [],
            assignee: null,
            maxResults: 30,
            TestContext.Current.CancellationToken);

        capturedFilter.ShouldNotBeNull();
        capturedFilter!.State.ShouldBe(ItemStateFilter.Open);
    }

    [Fact]
    public async Task ExecuteAsync_ClampsPageSize()
    {
        ApiOptions? capturedOptions = null;

        _gitHubClient.Issue
            .GetAllForRepository(
                "owner",
                "repo",
                Arg.Any<RepositoryIssueRequest>(),
                Arg.Do<ApiOptions>(o => capturedOptions = o))
            .Returns(Array.Empty<Issue>());

        await _skill.ExecuteAsync(
            "owner", "repo",
            state: null, labels: [], assignee: null,
            maxResults: 10_000,
            TestContext.Current.CancellationToken);

        capturedOptions.ShouldNotBeNull();
        capturedOptions!.PageSize.ShouldBe(100);
    }
}