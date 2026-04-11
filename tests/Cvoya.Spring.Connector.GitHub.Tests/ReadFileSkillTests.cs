// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Reflection;

using Cvoya.Spring.Connector.GitHub.Skills;

using FluentAssertions;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Xunit;

public class ReadFileSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly ReadFileSkill _skill;

    public ReadFileSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        var logger = Substitute.For<ILogger>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);
        _skill = new ReadFileSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsFileContent()
    {
        var fakeContent = CreateRepositoryContentViaReflection("README.md", "README.md", "# Hello");
        _gitHubClient.Repository.Content
            .GetAllContents("owner", "repo", "README.md")
            .Returns([fakeContent]);

        var result = await _skill.ExecuteAsync("owner", "repo", "README.md",
            cancellationToken: TestContext.Current.CancellationToken);

        result.GetProperty("name").GetString().Should().Be("README.md");
        result.GetProperty("path").GetString().Should().Be("README.md");
        result.GetProperty("content").GetString().Should().Be("# Hello");
    }

    private static RepositoryContent CreateRepositoryContentViaReflection(string name, string path, string content)
    {
        // RepositoryContent constructor: name, path, sha, size, type,
        // downloadUrl, url, gitUrl, htmlUrl, encoding, encodedContent, target, submoduleGitUrl
        // The Content property is decoded from encodedContent when encoding is "base64".
        var encodedContent = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(content));

        return new RepositoryContent(
            name: name,
            path: path,
            sha: "abc123",
            size: content.Length,
            type: ContentType.File,
            downloadUrl: string.Empty,
            url: string.Empty,
            gitUrl: string.Empty,
            htmlUrl: string.Empty,
            encoding: "base64",
            encodedContent: encodedContent,
            target: string.Empty,
            submoduleGitUrl: string.Empty);
    }
}