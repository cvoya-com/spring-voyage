// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class ListCommentsSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly ListCommentsSkill _skill;

    public ListCommentsSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new ListCommentsSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsProjectedComments()
    {
        ApiOptions? capturedOptions = null;
        _gitHubClient.Issue.Comment
            .GetAllForIssue("owner", "repo", 7L, Arg.Do<ApiOptions>(o => capturedOptions = o))
            .Returns(new[]
            {
                PrTestHelpers.CreateComment(1, "first", authorLogin: "alice"),
                PrTestHelpers.CreateComment(2, "second", authorLogin: "bob"),
            });

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 7, maxResults: 50,
            TestContext.Current.CancellationToken);

        capturedOptions.ShouldNotBeNull();
        capturedOptions!.PageSize.ShouldBe(50);

        result.GetProperty("count").GetInt32().ShouldBe(2);
        var comments = result.GetProperty("comments");
        comments.GetArrayLength().ShouldBe(2);
        comments[0].GetProperty("author").GetString().ShouldBe("alice");
        comments[1].GetProperty("body").GetString().ShouldBe("second");
    }

    [Fact]
    public async Task ExecuteAsync_ClampsPageSize()
    {
        ApiOptions? capturedOptions = null;
        _gitHubClient.Issue.Comment
            .GetAllForIssue("owner", "repo", 7L, Arg.Do<ApiOptions>(o => capturedOptions = o))
            .Returns(Array.Empty<IssueComment>());

        await _skill.ExecuteAsync(
            "owner", "repo", 7, maxResults: 10_000,
            TestContext.Current.CancellationToken);

        capturedOptions.ShouldNotBeNull();
        capturedOptions!.PageSize.ShouldBe(100);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyResult_Returns_CountZero()
    {
        _gitHubClient.Issue.Comment
            .GetAllForIssue("owner", "repo", 7L, Arg.Any<ApiOptions>())
            .Returns(Array.Empty<IssueComment>());

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 7, maxResults: 30,
            TestContext.Current.CancellationToken);

        result.GetProperty("count").GetInt32().ShouldBe(0);
        result.GetProperty("comments").GetArrayLength().ShouldBe(0);
    }
}
