// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.OAuth;

using Cvoya.Spring.Connector.GitHub.Auth.OAuth;
using Cvoya.Spring.Core.Secrets;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

public class GitHubOAuthClientFactoryTests
{
    [Fact]
    public async Task CreateAsync_KnownSession_ReturnsClient()
    {
        var sessionStore = new InMemoryOAuthSessionStore();
        var secretStore = Substitute.For<ISecretStore>();
        secretStore.ReadAsync("access-key", Arg.Any<CancellationToken>()).Returns("ghu_abc");
        await sessionStore.SaveAsync(new OAuthSession(
            SessionId: "sess-1",
            Login: "octocat",
            UserId: 42,
            Scopes: "repo",
            AccessTokenStoreKey: "access-key",
            RefreshTokenStoreKey: null,
            ExpiresAt: null,
            CreatedAt: DateTimeOffset.UtcNow,
            ClientState: null), TestContext.Current.CancellationToken);

        var factory = new GitHubOAuthClientFactory(sessionStore, secretStore, NullLoggerFactory.Instance);

        var client = await factory.CreateAsync("sess-1", TestContext.Current.CancellationToken);

        client.ShouldNotBeNull();
        client.Connection.Credentials.Password.ShouldBe("ghu_abc");
    }

    [Fact]
    public async Task CreateAsync_UnknownSession_Throws()
    {
        var factory = new GitHubOAuthClientFactory(
            new InMemoryOAuthSessionStore(),
            Substitute.For<ISecretStore>(),
            NullLoggerFactory.Instance);

        await Should.ThrowAsync<GitHubOAuthSessionNotFoundException>(() =>
            factory.CreateAsync("none", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateAsync_TokenPurgedFromStore_Throws()
    {
        var sessionStore = new InMemoryOAuthSessionStore();
        await sessionStore.SaveAsync(new OAuthSession(
            SessionId: "sess-orphan",
            Login: "octocat",
            UserId: 1,
            Scopes: "",
            AccessTokenStoreKey: "gone-key",
            RefreshTokenStoreKey: null,
            ExpiresAt: null,
            CreatedAt: DateTimeOffset.UtcNow,
            ClientState: null), TestContext.Current.CancellationToken);

        var secretStore = Substitute.For<ISecretStore>();
        secretStore.ReadAsync("gone-key", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<string?>(null));

        var factory = new GitHubOAuthClientFactory(sessionStore, secretStore, NullLoggerFactory.Instance);

        await Should.ThrowAsync<GitHubOAuthSessionNotFoundException>(() =>
            factory.CreateAsync("sess-orphan", TestContext.Current.CancellationToken));
    }
}