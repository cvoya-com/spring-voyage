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

public class TestWebhookSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly TestWebhookSkill _skill;

    public TestWebhookSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new TestWebhookSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_CallsTestEndpoint()
    {
        var result = await _skill.ExecuteAsync("owner", "repo", 7, TestContext.Current.CancellationToken);

        await _gitHubClient.Repository.Hooks.Received(1).Test("owner", "repo", 7);
        result.GetProperty("tested").GetBoolean().ShouldBeTrue();
        result.GetProperty("hook_id").GetInt64().ShouldBe(7);
    }

    [Fact]
    public async Task ExecuteAsync_HookMissing_BubblesNotFound()
    {
        _gitHubClient.Repository.Hooks
            .Test("owner", "repo", 404)
            .Returns(_ => throw new NotFoundException("gone", HttpStatusCode.NotFound));

        var act = () => _skill.ExecuteAsync("owner", "repo", 404, TestContext.Current.CancellationToken);

        await Should.ThrowAsync<NotFoundException>(act);
    }
}
