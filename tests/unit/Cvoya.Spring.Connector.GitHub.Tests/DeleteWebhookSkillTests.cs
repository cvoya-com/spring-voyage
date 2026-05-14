// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Net;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

public class DeleteWebhookSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly DeleteWebhookSkill _skill;

    public DeleteWebhookSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new DeleteWebhookSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_DeletesHook_ReturnsDeletedTrue()
    {
        var result = await _skill.ExecuteAsync("owner", "repo", 42, TestContext.Current.CancellationToken);

        await _gitHubClient.Repository.Hooks.Received(1).Delete("owner", "repo", 42);
        result.GetProperty("deleted").GetBoolean().ShouldBeTrue();
        result.GetProperty("hook_id").GetInt64().ShouldBe(42);
    }

    [Fact]
    public async Task ExecuteAsync_HookMissing_ReturnsNotFoundResult()
    {
        _gitHubClient.Repository.Hooks
            .Delete("owner", "repo", 99)
            .Returns(_ => throw new NotFoundException("gone", HttpStatusCode.NotFound));

        var result = await _skill.ExecuteAsync("owner", "repo", 99, TestContext.Current.CancellationToken);

        result.GetProperty("deleted").GetBoolean().ShouldBeFalse();
        result.GetProperty("reason").GetString().ShouldBe("not_found");
    }
}
