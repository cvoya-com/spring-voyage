// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Net;

using Cvoya.Spring.Connector.GitHub.Skills;

using FluentAssertions;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Xunit;

public class DeleteFileSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly DeleteFileSkill _skill;

    public DeleteFileSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        var logger = Substitute.For<ILogger>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);
        _skill = new DeleteFileSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_FileExists_CallsDeleteFileWithSha()
    {
        var existing = new RepositoryContent(
            name: "OLD.md",
            path: "docs/OLD.md",
            sha: "sha-old",
            size: 0,
            type: ContentType.File,
            downloadUrl: string.Empty,
            url: string.Empty,
            gitUrl: string.Empty,
            htmlUrl: string.Empty,
            encoding: "base64",
            encodedContent: Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("x")),
            target: string.Empty,
            submoduleGitUrl: string.Empty);

        _gitHubClient.Repository.Content
            .GetAllContentsByRef("owner", "repo", "docs/OLD.md", "main")
            .Returns([existing]);

        var result = await _skill.ExecuteAsync(
            "owner", "repo", "docs/OLD.md", "remove doc", "main",
            TestContext.Current.CancellationToken);

        result.GetProperty("action").GetString().Should().Be("deleted");
        result.GetProperty("previous_sha").GetString().Should().Be("sha-old");

        await _gitHubClient.Repository.Content.Received(1)
            .DeleteFile("owner", "repo", "docs/OLD.md", Arg.Is<DeleteFileRequest>(
                r => r.Sha == "sha-old" && r.Branch == "main" && r.Message == "remove doc"));
    }

    [Fact]
    public async Task ExecuteAsync_FileMissing_ThrowsNotFoundException()
    {
        _gitHubClient.Repository.Content
            .GetAllContentsByRef("owner", "repo", "missing.md", "main")
            .Returns<IReadOnlyList<RepositoryContent>>(
                _ => throw new NotFoundException("nope", HttpStatusCode.NotFound));

        var act = () => _skill.ExecuteAsync(
            "owner", "repo", "missing.md", "msg", "main",
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<NotFoundException>();
    }
}