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

public class UpdateWebhookSkillTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly UpdateWebhookSkill _skill;

    public UpdateWebhookSkillTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _skill = new UpdateWebhookSkill(_gitHubClient, loggerFactory);
    }

    [Fact]
    public async Task ExecuteAsync_UpdatesEventsActiveAndConfig()
    {
        EditRepositoryHook? captured = null;
        _gitHubClient.Repository.Hooks
            .Edit("owner", "repo", 42, Arg.Do<EditRepositoryHook>(e => captured = e))
            .Returns(WebhookTestHelpers.CreateRepositoryHook(id: 42, events: new[] { "issues" }));

        var result = await _skill.ExecuteAsync(
            "owner", "repo", hookId: 42,
            events: new[] { "issues", "pull_request" },
            active: false,
            url: "https://example.com/new",
            contentType: "json",
            secret: "s3cret",
            insecureSsl: false,
            TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.Events.ShouldContain("issues");
        captured.Events.ShouldContain("pull_request");
        captured.Active.ShouldBe(false);
        captured.Config["url"].ShouldBe("https://example.com/new");
        captured.Config["content_type"].ShouldBe("json");
        captured.Config["secret"].ShouldBe("s3cret");
        captured.Config["insecure_ssl"].ShouldBe("0");

        result.GetProperty("id").GetInt32().ShouldBe(42);
    }

    [Fact]
    public async Task ExecuteAsync_OmittedParameters_LeaveFieldsUntouched()
    {
        EditRepositoryHook? captured = null;
        _gitHubClient.Repository.Hooks
            .Edit("owner", "repo", 42, Arg.Do<EditRepositoryHook>(e => captured = e))
            .Returns(WebhookTestHelpers.CreateRepositoryHook(id: 42));

        await _skill.ExecuteAsync(
            "owner", "repo", hookId: 42,
            events: null,
            active: null,
            url: null,
            contentType: null,
            secret: null,
            insecureSsl: null,
            TestContext.Current.CancellationToken);

        captured.ShouldNotBeNull();
        captured!.Events.ShouldBeNull();
        captured.Active.ShouldBeNull();
        captured.Config.ShouldBeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_HookNotFound_BubblesNotFound()
    {
        _gitHubClient.Repository.Hooks
            .Edit("owner", "repo", 99, Arg.Any<EditRepositoryHook>())
            .ThrowsAsync(new NotFoundException("gone", HttpStatusCode.NotFound));

        var act = () => _skill.ExecuteAsync(
            "owner", "repo", hookId: 99,
            events: null, active: true, url: null,
            contentType: null, secret: null, insecureSsl: null,
            TestContext.Current.CancellationToken);

        await Should.ThrowAsync<NotFoundException>(act);
    }

    [Fact]
    public async Task ExecuteAsync_Validation422_BubblesApiValidationException()
    {
        _gitHubClient.Repository.Hooks
            .Edit("owner", "repo", 99, Arg.Any<EditRepositoryHook>())
            .ThrowsAsync(new ApiValidationException());

        var act = () => _skill.ExecuteAsync(
            "owner", "repo", hookId: 99,
            events: new[] { "not-a-real-event" },
            active: null, url: null,
            contentType: null, secret: null, insecureSsl: null,
            TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ApiValidationException>(act);
    }
}
