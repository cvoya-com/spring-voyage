// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class UpdateCommentSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly UpdateCommentSkill _skill;

    public UpdateCommentSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new UpdateCommentSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_CallsOctokit_ReturnsUpdatedBody()
    {
        _gitHubClient.Issue.Comment
            .Update("owner", "repo", 42L, "new body")
            .Returns(PrTestHelpers.CreateComment(
                id: 42,
                body: "new body",
                htmlUrl: "https://github.com/owner/repo/issues/1#issuecomment-42",
                updatedAt: DateTimeOffset.UtcNow));

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 42, "new body",
            TestContext.Current.CancellationToken);

        result.GetProperty("id").GetInt64().ShouldBe(42);
        result.GetProperty("body").GetString().ShouldBe("new body");
        result.GetProperty("html_url").GetString()
            .ShouldBe("https://github.com/owner/repo/issues/1#issuecomment-42");
    }

    [Fact]
    public async Task ExecuteAsync_OctokitThrowsNotFound_Propagates()
    {
        _gitHubClient.Issue.Comment
            .Update("owner", "repo", 99L, Arg.Any<string>())
            .Returns<IssueComment>(_ => throw new NotFoundException("no comment", System.Net.HttpStatusCode.NotFound));

        await Should.ThrowAsync<NotFoundException>(() =>
            _skill.ExecuteAsync("owner", "repo", 99, "body", TestContext.Current.CancellationToken));
    }
}
