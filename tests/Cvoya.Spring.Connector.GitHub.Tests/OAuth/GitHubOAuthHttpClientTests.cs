// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.OAuth;

using System.Net;
using System.Net.Http;

using Cvoya.Spring.Connector.GitHub.Auth.OAuth;
using Cvoya.Spring.Connector.GitHub.Tests.RateLimit;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Exercises the response parser and request wiring on
/// <see cref="GitHubOAuthHttpClient"/> against a stub
/// <see cref="HttpMessageHandler"/>. Confirms the JSON and form responses
/// round-trip into <see cref="OAuthTokenExchangeResult"/>.
/// </summary>
public class GitHubOAuthHttpClientTests
{
    [Fact]
    public void Parse_JsonBody_ExtractsTokenAndScopes()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        var client = new GitHubOAuthHttpClient(
            Substitute.For<IHttpClientFactory>(),
            NullLoggerFactory.Instance,
            time);

        var body = """
        {
          "access_token": "ghu_abc",
          "refresh_token": "ghr_xyz",
          "expires_in": 28800,
          "scope": "repo user:email",
          "token_type": "bearer"
        }
        """;

        var result = client.Parse(body);

        result.AccessToken.ShouldBe("ghu_abc");
        result.RefreshToken.ShouldBe("ghr_xyz");
        result.GrantedScopes.ShouldBe("repo user:email");
        result.ExpiresAt.ShouldNotBeNull();
        result.ExpiresAt!.Value.ShouldBe(now.AddSeconds(28800), TimeSpan.FromSeconds(1));
        result.Error.ShouldBeNull();
    }

    [Fact]
    public void Parse_JsonErrorBody_SurfacesError()
    {
        var client = new GitHubOAuthHttpClient(
            Substitute.For<IHttpClientFactory>(),
            NullLoggerFactory.Instance,
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        var body = """
        {
          "error": "bad_verification_code",
          "error_description": "The code passed is incorrect or expired.",
          "error_uri": "https://docs.github.com/..."
        }
        """;

        var result = client.Parse(body);

        result.AccessToken.ShouldBeNull();
        result.Error.ShouldBe("bad_verification_code");
        result.ErrorDescription.ShouldBe("The code passed is incorrect or expired.");
    }

    [Fact]
    public void Parse_UrlEncodedBody_ExtractsToken()
    {
        var client = new GitHubOAuthHttpClient(
            Substitute.For<IHttpClientFactory>(),
            NullLoggerFactory.Instance,
            new FakeTimeProvider(DateTimeOffset.UtcNow));

        var body = "access_token=ghu_form&scope=repo&token_type=bearer";

        var result = client.Parse(body);

        result.AccessToken.ShouldBe("ghu_form");
        result.GrantedScopes.ShouldBe("repo");
        result.Error.ShouldBeNull();
    }

    [Fact]
    public async Task ExchangeCodeAsync_RoutesThroughNamedHttpClient()
    {
        string? sentBody = null;
        var handler = new StubHandler((req, _) =>
        {
            sentBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"ghu_x","scope":"repo","token_type":"bearer"}""",
                    System.Text.Encoding.UTF8, "application/json"),
            };
        });

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(GitHubOAuthHttpClient.HttpClientName)
            .Returns(_ => new HttpClient(handler, disposeHandler: false));

        var client = new GitHubOAuthHttpClient(factory, NullLoggerFactory.Instance);

        var result = await client.ExchangeCodeAsync(
            "cid", "csec", "the-code", "https://example.com/cb",
            TestContext.Current.CancellationToken);

        result.AccessToken.ShouldBe("ghu_x");
        sentBody.ShouldNotBeNull();
        sentBody!.ShouldContain("code=the-code");
        sentBody.ShouldContain("client_id=cid");
        sentBody.ShouldContain("client_secret=csec");
        sentBody.ShouldContain("redirect_uri=");
    }

    [Fact]
    public async Task RevokeTokenAsync_204_ReturnsTrue()
    {
        var handler = new StubHandler((_, __) => new HttpResponseMessage(HttpStatusCode.NoContent));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(GitHubOAuthHttpClient.HttpClientName)
            .Returns(_ => new HttpClient(handler, disposeHandler: false));
        var client = new GitHubOAuthHttpClient(factory, NullLoggerFactory.Instance);

        var ok = await client.RevokeTokenAsync("cid", "csec", "ghu_abc", TestContext.Current.CancellationToken);

        ok.ShouldBeTrue();
    }

    [Fact]
    public async Task RevokeTokenAsync_404_ReturnsTrue()
    {
        var handler = new StubHandler((_, __) => new HttpResponseMessage(HttpStatusCode.NotFound));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(GitHubOAuthHttpClient.HttpClientName)
            .Returns(_ => new HttpClient(handler, disposeHandler: false));
        var client = new GitHubOAuthHttpClient(factory, NullLoggerFactory.Instance);

        var ok = await client.RevokeTokenAsync("cid", "csec", "ghu_abc", TestContext.Current.CancellationToken);

        ok.ShouldBeTrue();
    }

    [Fact]
    public async Task RevokeTokenAsync_500_ReturnsFalse()
    {
        var handler = new StubHandler((_, __) => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(GitHubOAuthHttpClient.HttpClientName)
            .Returns(_ => new HttpClient(handler, disposeHandler: false));
        var client = new GitHubOAuthHttpClient(factory, NullLoggerFactory.Instance);

        var ok = await client.RevokeTokenAsync("cid", "csec", "ghu_abc", TestContext.Current.CancellationToken);

        ok.ShouldBeFalse();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request, cancellationToken));
    }
}