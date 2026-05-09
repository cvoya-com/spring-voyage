// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Net;

using Cvoya.Spring.Connector.GitHub.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Octokit;

using Shouldly;

using Xunit;

public class ListWebhooksSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly ListWebhooksSkill _skill;

    public ListWebhooksSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new ListWebhooksSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsHooksProjection()
    {
        var hooks = new[]
        {
            WebhookTestHelpers.CreateRepositoryHook(
                id: 11,
                name: "web",
                active: true,
                events: new[] { "issues", "pull_request" },
                config: new Dictionary<string, string>
                {
                    ["url"] = "https://example.com/hook",
                    ["content_type"] = "json",
                    ["insecure_ssl"] = "0",
                }),
            WebhookTestHelpers.CreateRepositoryHook(
                id: 22,
                name: "web",
                active: false,
                events: new[] { "push" }),
        };

        _gitHubClient.Repository.Hooks.GetAll("owner", "repo").Returns(hooks);

        var result = await _skill.ExecuteAsync("owner", "repo", TestContext.Current.CancellationToken);

        result.GetProperty("count").GetInt32().ShouldBe(2);
        result.GetProperty("hooks").GetArrayLength().ShouldBe(2);

        var first = result.GetProperty("hooks")[0];
        first.GetProperty("id").GetInt32().ShouldBe(11);
        first.GetProperty("active").GetBoolean().ShouldBeTrue();
        first.GetProperty("events").GetArrayLength().ShouldBe(2);
        first.GetProperty("config").GetProperty("url").GetString().ShouldBe("https://example.com/hook");
    }

    [Fact]
    public async Task ExecuteAsync_RepoNotFound_BubblesNotFound()
    {
        _gitHubClient.Repository.Hooks
            .GetAll("owner", "missing")
            .ThrowsAsync(new NotFoundException("gone", HttpStatusCode.NotFound));

        var act = () => _skill.ExecuteAsync("owner", "missing", TestContext.Current.CancellationToken);

        await Should.ThrowAsync<NotFoundException>(act);
    }
}
