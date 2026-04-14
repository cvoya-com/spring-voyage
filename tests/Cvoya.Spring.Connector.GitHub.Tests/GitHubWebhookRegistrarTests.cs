// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using System.Net;
using System.Reflection;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.RateLimit;
using Cvoya.Spring.Connector.GitHub.Webhooks;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Octokit;

using Shouldly;

using Xunit;

/// <summary>
/// Exercises the Octokit-facing parts of <see cref="GitHubWebhookRegistrar"/>.
/// Uses a subclass that short-circuits the connector's
/// <see cref="GitHubConnector.CreateAuthenticatedClientAsync"/> so a mocked
/// <see cref="IGitHubClient"/> is injected without hitting GitHub's auth API.
/// </summary>
public class GitHubWebhookRegistrarTests
{
    private readonly IGitHubClient _gitHubClient;
    private readonly GitHubConnectorOptions _options;
    private readonly GitHubWebhookRegistrar _registrar;

    public GitHubWebhookRegistrarTests()
    {
        _gitHubClient = Substitute.For<IGitHubClient>();

        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        _options = new GitHubConnectorOptions
        {
            WebhookUrl = "https://example.com/api/v1/webhooks/github",
            WebhookSecret = "s3cret",
        };

        var auth = new GitHubAppAuth(_options, loggerFactory);
        var handler = new GitHubWebhookHandler(_options, loggerFactory);
        var signatureValidator = new WebhookSignatureValidator();
        var retryOptions = new GitHubRetryOptions();
        var tracker = new GitHubRateLimitTracker(retryOptions, loggerFactory);
        var connector = new FakeGitHubConnector(
            _gitHubClient,
            auth,
            handler,
            signatureValidator,
            _options,
            tracker,
            retryOptions,
            loggerFactory);

        _registrar = new GitHubWebhookRegistrar(connector, _options, loggerFactory);
    }

    [Fact]
    public async Task RegisterAsync_CreatesHook_WithExpectedPayload()
    {
        var hook = CreateRepositoryHook(id: 42);
        _gitHubClient.Repository.Hooks
            .Create("owner", "repo", Arg.Any<NewRepositoryHook>())
            .Returns(hook);

        var result = await _registrar.RegisterAsync("owner", "repo", TestContext.Current.CancellationToken);

        result.ShouldBe(42);

        await _gitHubClient.Repository.Hooks.Received(1)
            .Create("owner", "repo", Arg.Is<NewRepositoryHook>(h =>
                h.Name == "web" &&
                h.Active == true &&
                h.Events.Contains("issues") &&
                h.Events.Contains("pull_request") &&
                h.Events.Contains("issue_comment") &&
                h.Events.Contains("pull_request_review") &&
                h.Events.Contains("pull_request_review_comment") &&
                h.Events.Contains("pull_request_review_thread") &&
                h.Config["url"] == "https://example.com/api/v1/webhooks/github" &&
                h.Config["secret"] == "s3cret" &&
                h.Config["content_type"] == "json"));
    }

    [Fact]
    public async Task RegisterAsync_MissingWebhookUrl_Throws()
    {
        _options.WebhookUrl = string.Empty;

        var act = () => _registrar.RegisterAsync("owner", "repo", TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<InvalidOperationException>(act);
        ex.Message.ShouldContain("WebhookUrl");
    }

    [Fact]
    public async Task UnregisterAsync_CallsDelete()
    {
        await _registrar.UnregisterAsync("owner", "repo", hookId: 42, TestContext.Current.CancellationToken);

        await _gitHubClient.Repository.Hooks.Received(1).Delete("owner", "repo", 42);
    }

    [Fact]
    public async Task UnregisterAsync_NotFound_SwallowsException()
    {
        _gitHubClient.Repository.Hooks
            .Delete("owner", "repo", 99)
            .Returns(_ => throw new NotFoundException("gone", HttpStatusCode.NotFound));

        // Should not throw — a stale hook id must not block /stop teardown.
        await _registrar.UnregisterAsync("owner", "repo", hookId: 99, TestContext.Current.CancellationToken);

        await _gitHubClient.Repository.Hooks.Received(1).Delete("owner", "repo", 99);
    }

    private static RepositoryHook CreateRepositoryHook(int id)
    {
        var ctor = typeof(RepositoryHook)
            .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderByDescending(c => c.GetParameters().Length)
            .First();

        var args = ctor.GetParameters().Select(p =>
        {
            if (p.Name == "id") return (object?)id;
            if (p.ParameterType == typeof(string)) return string.Empty;
            if (p.ParameterType == typeof(long)) return 0L;
            if (p.ParameterType == typeof(int)) return 0;
            if (p.ParameterType == typeof(bool)) return false;
            if (p.ParameterType == typeof(DateTimeOffset)) return DateTimeOffset.UtcNow;
            if (p.ParameterType.IsValueType) return Activator.CreateInstance(p.ParameterType);
            return null;
        }).ToArray();

        return (RepositoryHook)ctor.Invoke(args);
    }

    /// <summary>
    /// Stub connector that returns a pre-built <see cref="IGitHubClient"/>
    /// instead of exchanging a JWT for an installation token. The registrar
    /// only calls <see cref="GitHubConnector.CreateAuthenticatedClientAsync"/>
    /// to get a client, so overriding that method is enough.
    /// </summary>
    private sealed class FakeGitHubConnector : GitHubConnector
    {
        private readonly IGitHubClient _client;

        public FakeGitHubConnector(
            IGitHubClient client,
            GitHubAppAuth auth,
            GitHubWebhookHandler handler,
            IWebhookSignatureValidator signatureValidator,
            GitHubConnectorOptions options,
            IGitHubRateLimitTracker rateLimitTracker,
            GitHubRetryOptions retryOptions,
            ILoggerFactory loggerFactory)
            : base(auth, handler, signatureValidator, options, rateLimitTracker, retryOptions, loggerFactory)
        {
            _client = client;
        }

        public override Task<IGitHubClient> CreateAuthenticatedClientAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(_client);
    }
}