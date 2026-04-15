// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.OAuth;

using System.Collections.Concurrent;

using Cvoya.Spring.Connector.GitHub.Auth.OAuth;
using Cvoya.Spring.Connector.GitHub.Tests.RateLimit;
using Cvoya.Spring.Core.Secrets;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// End-to-end tests for <see cref="GitHubOAuthService"/> covering the
/// authorize → callback → revoke → session-lookup flow. Uses a stub HTTP
/// client so nothing touches the network.
/// </summary>
public class GitHubOAuthServiceTests
{
    private static GitHubOAuthService CreateService(
        GitHubOAuthOptions? options = null,
        IGitHubOAuthHttpClient? oauthHttp = null,
        ISecretStore? secretStore = null,
        IOAuthStateStore? stateStore = null,
        IOAuthSessionStore? sessionStore = null,
        IGitHubUserFetcher? userFetcher = null,
        FakeTimeProvider? timeProvider = null)
    {
        options ??= new GitHubOAuthOptions
        {
            ClientId = "client-id",
            ClientSecret = "client-secret",
            RedirectUri = "https://example.com/cb",
            Scopes = new List<string> { "repo" },
            StateTtl = TimeSpan.FromMinutes(10),
        };
        oauthHttp ??= Substitute.For<IGitHubOAuthHttpClient>();
        secretStore ??= CreateInMemorySecretStore();
        stateStore ??= new InMemoryOAuthStateStore(NullLoggerFactory.Instance, timeProvider);
        sessionStore ??= new InMemoryOAuthSessionStore();
        userFetcher ??= Substitute.For<IGitHubUserFetcher>();

        return new GitHubOAuthService(
            stateStore,
            sessionStore,
            oauthHttp,
            secretStore,
            new OptionsMonitorStub(options),
            userFetcher,
            NullLoggerFactory.Instance,
            timeProvider);
    }

    private static ISecretStore CreateInMemorySecretStore()
    {
        var values = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var store = Substitute.For<ISecretStore>();
        store.WriteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var plaintext = (string)ci[0];
                var key = Guid.NewGuid().ToString("N");
                values[key] = plaintext;
                return Task.FromResult(key);
            });
        store.ReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var key = (string)ci[0];
                values.TryGetValue(key, out var value);
                return Task.FromResult<string?>(value);
            });
        store.DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var key = (string)ci[0];
                values.TryRemove(key, out _);
                return Task.CompletedTask;
            });
        return store;
    }

    [Fact]
    public async Task BeginAuthorizationAsync_ProducesAuthorizeUrlAndStoresState()
    {
        var stateStore = new InMemoryOAuthStateStore(NullLoggerFactory.Instance);
        var service = CreateService(stateStore: stateStore);
        var ct = TestContext.Current.CancellationToken;

        var result = await service.BeginAuthorizationAsync(
            scopesOverride: null,
            clientState: "resume-after-link",
            ct);

        result.AuthorizeUrl.ShouldStartWith("https://github.com/login/oauth/authorize?");
        result.AuthorizeUrl.ShouldContain("client_id=client-id");
        result.AuthorizeUrl.ShouldContain($"state={Uri.EscapeDataString(result.State)}");
        result.AuthorizeUrl.ShouldContain("redirect_uri=" + Uri.EscapeDataString("https://example.com/cb"));
        result.AuthorizeUrl.ShouldContain("scope=repo");

        // State is consumable — proves it was persisted.
        var entry = await stateStore.ConsumeAsync(result.State, ct);
        entry.ShouldNotBeNull();
        entry!.ClientState.ShouldBe("resume-after-link");
        entry.Scopes.ShouldBe("repo");
    }

    [Fact]
    public async Task BeginAuthorizationAsync_ScopesOverride_UsesOverride()
    {
        var service = CreateService();
        var ct = TestContext.Current.CancellationToken;

        var result = await service.BeginAuthorizationAsync(
            scopesOverride: new[] { "user:email", "read:org" },
            clientState: null,
            ct);

        result.AuthorizeUrl.ShouldContain("scope=" + Uri.EscapeDataString("user:email read:org"));
    }

    [Fact]
    public async Task BeginAuthorizationAsync_UnconfiguredClientId_Throws()
    {
        var service = CreateService(options: new GitHubOAuthOptions
        {
            ClientId = string.Empty,
            RedirectUri = "https://example.com/cb",
        });

        await Should.ThrowAsync<InvalidOperationException>(() =>
            service.BeginAuthorizationAsync(null, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HandleCallbackAsync_HappyPath_IssuesSessionAndStoresToken()
    {
        var oauthHttp = Substitute.For<IGitHubOAuthHttpClient>();
        oauthHttp.ExchangeCodeAsync("client-id", "client-secret", "the-code", "https://example.com/cb", Arg.Any<CancellationToken>())
            .Returns(new OAuthTokenExchangeResult(
                AccessToken: "ghu_abc",
                RefreshToken: "ghr_xyz",
                ExpiresAt: DateTimeOffset.UtcNow.AddHours(8),
                GrantedScopes: "repo",
                Error: null,
                ErrorDescription: null));

        var userFetcher = Substitute.For<IGitHubUserFetcher>();
        userFetcher.GetAsync("ghu_abc", Arg.Any<CancellationToken>())
            .Returns(new GitHubUserIdentity("octocat", 42, "Octo Cat", "octo@example.com"));

        var secretStore = CreateInMemorySecretStore();
        var sessionStore = new InMemoryOAuthSessionStore();
        var stateStore = new InMemoryOAuthStateStore(NullLoggerFactory.Instance);
        var service = CreateService(
            oauthHttp: oauthHttp,
            secretStore: secretStore,
            sessionStore: sessionStore,
            stateStore: stateStore,
            userFetcher: userFetcher);
        var ct = TestContext.Current.CancellationToken;

        var auth = await service.BeginAuthorizationAsync(null, null, ct);
        var callback = await service.HandleCallbackAsync("the-code", auth.State, ct);

        callback.SessionId.ShouldNotBeNull();
        callback.Login.ShouldBe("octocat");
        callback.Error.ShouldBeNull();

        // Session is retrievable.
        var session = await sessionStore.GetAsync(callback.SessionId!, ct);
        session.ShouldNotBeNull();
        session!.Login.ShouldBe("octocat");
        session.UserId.ShouldBe(42);
        session.RefreshTokenStoreKey.ShouldNotBeNull();

        // Token plaintext is retrievable via the secret store using the
        // opaque key on the session — never directly exposed.
        var storedAccess = await secretStore.ReadAsync(session.AccessTokenStoreKey, ct);
        storedAccess.ShouldBe("ghu_abc");

        // Replaying the same state fails — it was consumed.
        var replay = await service.HandleCallbackAsync("the-code", auth.State, ct);
        replay.SessionId.ShouldBeNull();
        replay.Error.ShouldBe("invalid_state");
    }

    [Fact]
    public async Task HandleCallbackAsync_UnknownState_ReturnsInvalidState()
    {
        var service = CreateService();
        var ct = TestContext.Current.CancellationToken;

        var callback = await service.HandleCallbackAsync("the-code", "never-issued", ct);

        callback.SessionId.ShouldBeNull();
        callback.Error.ShouldBe("invalid_state");
    }

    [Fact]
    public async Task HandleCallbackAsync_ExchangeReturnsError_SurfacesErrorCode()
    {
        var oauthHttp = Substitute.For<IGitHubOAuthHttpClient>();
        oauthHttp.ExchangeCodeAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new OAuthTokenExchangeResult(
                AccessToken: null,
                RefreshToken: null,
                ExpiresAt: null,
                GrantedScopes: string.Empty,
                Error: "bad_verification_code",
                ErrorDescription: "The code is incorrect or expired."));

        var service = CreateService(oauthHttp: oauthHttp);
        var ct = TestContext.Current.CancellationToken;

        var auth = await service.BeginAuthorizationAsync(null, null, ct);
        var callback = await service.HandleCallbackAsync("bad-code", auth.State, ct);

        callback.SessionId.ShouldBeNull();
        callback.Error.ShouldBe("bad_verification_code");
        callback.ErrorDescription.ShouldBe("The code is incorrect or expired.");
    }

    [Fact]
    public async Task HandleCallbackAsync_MissingCode_ReturnsInvalidRequest()
    {
        var service = CreateService();
        var ct = TestContext.Current.CancellationToken;

        var callback = await service.HandleCallbackAsync(string.Empty, "some-state", ct);

        callback.Error.ShouldBe("invalid_request");
    }

    [Fact]
    public async Task RevokeAsync_UnknownSession_ReturnsFalse()
    {
        var service = CreateService();
        var ct = TestContext.Current.CancellationToken;

        var revoked = await service.RevokeAsync("no-such-session", ct);

        revoked.ShouldBeFalse();
    }

    [Fact]
    public async Task RevokeAsync_KnownSession_CallsGitHubRevokeAndDeletesSession()
    {
        var oauthHttp = Substitute.For<IGitHubOAuthHttpClient>();
        oauthHttp.ExchangeCodeAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new OAuthTokenExchangeResult("ghu_abc", null, null, "repo", null, null));
        oauthHttp.RevokeTokenAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(true);

        var userFetcher = Substitute.For<IGitHubUserFetcher>();
        userFetcher.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GitHubUserIdentity("octocat", 42, null, null));

        var sessionStore = new InMemoryOAuthSessionStore();
        var service = CreateService(
            oauthHttp: oauthHttp,
            sessionStore: sessionStore,
            userFetcher: userFetcher);
        var ct = TestContext.Current.CancellationToken;

        var auth = await service.BeginAuthorizationAsync(null, null, ct);
        var callback = await service.HandleCallbackAsync("code", auth.State, ct);

        var revoked = await service.RevokeAsync(callback.SessionId!, ct);
        revoked.ShouldBeTrue();

        await oauthHttp.Received(1).RevokeTokenAsync(
            "client-id", "client-secret", "ghu_abc", Arg.Any<CancellationToken>());
        (await sessionStore.GetAsync(callback.SessionId!, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task GetSessionAsync_UnknownId_ReturnsNull()
    {
        var service = CreateService();
        var ct = TestContext.Current.CancellationToken;

        var session = await service.GetSessionAsync("missing", ct);
        session.ShouldBeNull();
    }

    private sealed class OptionsMonitorStub : IOptionsMonitor<GitHubOAuthOptions>
    {
        private readonly GitHubOAuthOptions _value;

        public OptionsMonitorStub(GitHubOAuthOptions value) => _value = value;

        public GitHubOAuthOptions CurrentValue => _value;
        public GitHubOAuthOptions Get(string? name) => _value;
        public IDisposable? OnChange(Action<GitHubOAuthOptions, string?> listener) => null;
    }
}