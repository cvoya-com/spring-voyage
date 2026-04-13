// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Net;
using System.Reflection;

using Cvoya.Spring.Connector.GitHub.Skills;

using FluentAssertions;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Xunit;

public class WriteFileSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly WriteFileSkill _skill;

    public WriteFileSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        var logger = Substitute.For<ILogger>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(logger);
        _skill = new WriteFileSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_FileDoesNotExist_CallsCreateFile()
    {
        _gitHubClient.Repository.Content
            .GetAllContentsByRef("owner", "repo", "docs/new.md", "main")
            .Returns<IReadOnlyList<RepositoryContent>>(
                _ => throw new NotFoundException("absent", HttpStatusCode.NotFound));

        var changeSet = CreateChangeSet("sha-commit", "sha-content");
        _gitHubClient.Repository.Content
            .CreateFile("owner", "repo", "docs/new.md", Arg.Any<CreateFileRequest>())
            .Returns(changeSet);

        var result = await _skill.ExecuteAsync(
            "owner", "repo", "docs/new.md", "# hello", "add doc", "main",
            TestContext.Current.CancellationToken);

        result.GetProperty("action").GetString().Should().Be("created");
        result.GetProperty("commit_sha").GetString().Should().Be("sha-commit");
        result.GetProperty("content_sha").GetString().Should().Be("sha-content");

        // The ctor for CreateFileRequest base64-encodes the content by default,
        // so assert on the branch/message and the decoded content instead of
        // comparing against the raw string.
        await _gitHubClient.Repository.Content.Received(1)
            .CreateFile("owner", "repo", "docs/new.md", Arg.Is<CreateFileRequest>(
                r => r.Message == "add doc"
                    && r.Branch == "main"
                    && System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(r.Content)) == "# hello"));
    }

    [Fact]
    public async Task ExecuteAsync_FileExists_CallsUpdateFileWithSha()
    {
        var existing = CreateRepositoryContentStub("new.md", "docs/new.md", "existing-sha");
        _gitHubClient.Repository.Content
            .GetAllContentsByRef("owner", "repo", "docs/new.md", "main")
            .Returns([existing]);

        var changeSet = CreateChangeSet("sha-commit-2", "sha-content-2");
        _gitHubClient.Repository.Content
            .UpdateFile("owner", "repo", "docs/new.md", Arg.Any<UpdateFileRequest>())
            .Returns(changeSet);

        var result = await _skill.ExecuteAsync(
            "owner", "repo", "docs/new.md", "# updated", "update doc", "main",
            TestContext.Current.CancellationToken);

        result.GetProperty("action").GetString().Should().Be("updated");
        result.GetProperty("content_sha").GetString().Should().Be("sha-content-2");

        await _gitHubClient.Repository.Content.Received(1)
            .UpdateFile("owner", "repo", "docs/new.md", Arg.Is<UpdateFileRequest>(
                r => r.Sha == "existing-sha" && r.Branch == "main"));
    }

    private static RepositoryContent CreateRepositoryContentStub(string name, string path, string sha)
    {
        return new RepositoryContent(
            name: name,
            path: path,
            sha: sha,
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
    }

    private static RepositoryContentChangeSet CreateChangeSet(string commitSha, string contentSha)
    {
        var changeSet = CreateViaLargestCtor<RepositoryContentChangeSet>();

        var commit = CreateViaLargestCtor<Commit>();
        SetProperty(commit, "Sha", commitSha);
        SetProperty(changeSet, "Commit", commit);

        var content = CreateViaLargestCtor<RepositoryContentInfo>();
        SetProperty(content, "Sha", contentSha);
        SetProperty(changeSet, "Content", content);

        return changeSet;
    }

    private static T CreateViaLargestCtor<T>() where T : class
    {
        var ctor = typeof(T).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var args = ctor.GetParameters().Select(p =>
        {
            if (p.ParameterType == typeof(string)) return (object?)string.Empty;
            if (p.ParameterType == typeof(int)) return 0;
            if (p.ParameterType == typeof(long)) return 0L;
            if (p.ParameterType == typeof(bool)) return false;
            if (p.ParameterType == typeof(DateTimeOffset)) return DateTimeOffset.UtcNow;
            if (p.ParameterType.IsGenericType &&
                p.ParameterType.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                // Octokit's response records guard against null IEnumerables (e.g. Commit.parents).
                // Fall back to an empty array of the right element type.
                var elementType = p.ParameterType.GetGenericArguments()[0];
                return Array.CreateInstance(elementType, 0);
            }
            if (p.ParameterType.IsValueType) return Activator.CreateInstance(p.ParameterType);
            return null;
        }).ToArray();

        return (T)ctor.Invoke(args);
    }

    private static void SetProperty(object target, string propertyName, object value)
    {
        var prop = target.GetType().GetProperty(propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        prop!.SetValue(target, value);
    }
}