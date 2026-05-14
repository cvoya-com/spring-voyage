// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class MergePullRequestSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly MergePullRequestSkill _skill;

    public MergePullRequestSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new MergePullRequestSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_Squash_PassesCorrectRequest()
    {
        MergePullRequest? captured = null;
        _gitHubClient.PullRequest
            .Merge("owner", "repo", 5, Arg.Do<MergePullRequest>(m => captured = m))
            .Returns(PrTestHelpers.CreateMergeResult(merged: true, sha: "mergedSha", message: "Merged"));

        var result = await _skill.ExecuteAsync(
            "owner", "repo", 5,
            mergeMethod: "squash",
            commitTitle: "Title",
            commitMessage: "Message",
            sha: "expected",
            TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.MergeMethod.ShouldBe(PullRequestMergeMethod.Squash);
        captured.CommitTitle.ShouldBe("Title");
        captured.CommitMessage.ShouldBe("Message");
        captured.Sha.ShouldBe("expected");

        result.GetProperty("merged").GetBoolean().ShouldBeTrue();
        result.GetProperty("sha").GetString().ShouldBe("mergedSha");
    }

    [Fact]
    public async Task ExecuteAsync_DefaultMethodIsMerge()
    {
        MergePullRequest? captured = null;
        _gitHubClient.PullRequest
            .Merge("owner", "repo", 5, Arg.Do<MergePullRequest>(m => captured = m))
            .Returns(PrTestHelpers.CreateMergeResult(merged: true));

        await _skill.ExecuteAsync(
            "owner", "repo", 5,
            mergeMethod: null, commitTitle: null, commitMessage: null, sha: null,
            TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.MergeMethod.ShouldBe(PullRequestMergeMethod.Merge);
        captured.CommitTitle.ShouldBeNull();
        captured.Sha.ShouldBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_OctokitValidationThrows_Propagates()
    {
        _gitHubClient.PullRequest
            .Merge("owner", "repo", 5, Arg.Any<MergePullRequest>())
            .Returns<PullRequestMerge>(_ =>
                throw new ApiValidationException());

        await Should.ThrowAsync<ApiValidationException>(() =>
            _skill.ExecuteAsync(
                "owner", "repo", 5,
                mergeMethod: null, commitTitle: null, commitMessage: null, sha: null,
                TestContext.Current.CancellationToken));
    }
}
