// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class GetIssueAuthorSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly GetIssueAuthorSkill _skill;

    public GetIssueAuthorSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new GetIssueAuthorSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsAuthorLogin()
    {
        _gitHubClient.Issue.Get("owner", "repo", 99L)
            .Returns(IssueTestHelpers.CreateIssue(number: 99, authorLogin: "alice"));

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 99,
            TestContext.Current.CancellationToken);

        result.GetProperty("number").GetInt32().ShouldBe(99);
        result.GetProperty("author").GetString().ShouldBe("alice");
    }
}