// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Connector.GitHub;
using Cvoya.Spring.Connector.GitHub.Auth.OAuth;
using Cvoya.Spring.Connector.GitHub.Webhooks;
using Cvoya.Spring.Connectors;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the OAuth endpoints under
/// <c>/api/v1/connectors/github/oauth</c>. Substitutes the OAuth service so
/// the tests assert the HTTP translation layer, not the service internals —
/// those are covered by <c>GitHubOAuthServiceTests</c> directly.
/// </summary>
public class GitHubOAuthEndpointsTests
{
    [Fact]
    public async Task Authorize_ReturnsUrlAndState()
    {
        var service = Substitute.For<IGitHubOAuthService>();
        service.BeginAuthorizationAsync(Arg.Any<IReadOnlyList<string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new AuthorizeResult("https://github.com/login/oauth/authorize?state=abc", "abc"));

        await using var factory = CreateFactory(oauthService: service);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.PostAsJsonAsync(
            "/api/v1/tenant/connectors/github/oauth/authorize",
            new OAuthAuthorizeRequest(Scopes: new[] { "repo" }, ClientState: "resume"),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<OAuthAuthorizeResponse>(ct);
        body.ShouldNotBeNull();
        body!.AuthorizeUrl.ShouldStartWith("https://github.com/login/oauth/authorize");
        body.State.ShouldBe("abc");
    }

    [Fact]
    public async Task Authorize_Unconfigured_Returns502()
    {
        var service = Substitute.For<IGitHubOAuthService>();
        service.BeginAuthorizationAsync(Arg.Any<IReadOnlyList<string>?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("GitHub:OAuth:ClientId is not configured."));

        await using var factory = CreateFactory(oauthService: service);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.PostAsJsonAsync(
            "/api/v1/tenant/connectors/github/oauth/authorize",
            new OAuthAuthorizeRequest(null, null),
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task Callback_HappyPath_ReturnsSession()
    {
        var service = Substitute.For<IGitHubOAuthService>();
        service.HandleCallbackAsync("the-code", "the-state", Arg.Any<CancellationToken>())
            .Returns(new CallbackResult("sess-1", "octocat", null, null));

        await using var factory = CreateFactory(oauthService: service);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            "/api/v1/tenant/connectors/github/oauth/callback?code=the-code&state=the-state", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<OAuthCallbackResponse>(ct);
        body.ShouldNotBeNull();
        body!.SessionId.ShouldBe("sess-1");
        body.Login.ShouldBe("octocat");
    }

    [Fact]
    public async Task Callback_InvalidState_Returns400()
    {
        var service = Substitute.For<IGitHubOAuthService>();
        service.HandleCallbackAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new CallbackResult(null, null, "invalid_state", "Expired."));

        await using var factory = CreateFactory(oauthService: service);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            "/api/v1/tenant/connectors/github/oauth/callback?code=c&state=s", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Callback_GitHubError_Returns400WithErrorExtension()
    {
        // Caller-cancelled consent: GitHub redirects with ?error=access_denied.
        await using var factory = CreateFactory(oauthService: Substitute.For<IGitHubOAuthService>());
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            "/api/v1/tenant/connectors/github/oauth/callback?error=access_denied&error_description=User%20declined", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var content = await response.Content.ReadAsStringAsync(ct);
        content.ShouldContain("access_denied");
    }

    [Fact]
    public async Task Revoke_KnownSession_Returns204()
    {
        var service = Substitute.For<IGitHubOAuthService>();
        service.RevokeAsync("sess-1", Arg.Any<CancellationToken>()).Returns(true);

        await using var factory = CreateFactory(oauthService: service);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.PostAsync(
            "/api/v1/tenant/connectors/github/oauth/revoke/sess-1",
            content: null,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Revoke_UnknownSession_Returns404()
    {
        var service = Substitute.For<IGitHubOAuthService>();
        service.RevokeAsync("nope", Arg.Any<CancellationToken>()).Returns(false);

        await using var factory = CreateFactory(oauthService: service);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.PostAsync(
            "/api/v1/tenant/connectors/github/oauth/revoke/nope",
            content: null,
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetSession_Known_ReturnsMetadata()
    {
        var service = Substitute.For<IGitHubOAuthService>();
        service.GetSessionAsync("sess-1", Arg.Any<CancellationToken>())
            .Returns(new OAuthSession(
                SessionId: "sess-1",
                Login: "octocat",
                UserId: 42,
                Scopes: "repo",
                AccessTokenStoreKey: "opaque",
                RefreshTokenStoreKey: null,
                ExpiresAt: null,
                CreatedAt: DateTimeOffset.UtcNow,
                ClientState: "resume-target"));

        await using var factory = CreateFactory(oauthService: service);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync(
            "/api/v1/tenant/connectors/github/oauth/session/sess-1", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<OAuthSessionResponse>(ct);
        body.ShouldNotBeNull();
        body!.SessionId.ShouldBe("sess-1");
        body.Login.ShouldBe("octocat");
        body.UserId.ShouldBe(42L);
        body.Scopes.ShouldBe("repo");
        body.ClientState.ShouldBe("resume-target");

        // The response shape does not include any token store key fields —
        // those are internal to the connector.
        var raw = await response.Content.ReadAsStringAsync(ct);
        raw.ShouldNotBeNull();
        raw.ShouldNotContain("opaque");
        raw.ShouldNotContain("AccessTokenStoreKey", Case.Insensitive);
    }

    [Fact]
    public async Task GetSession_Unknown_Returns404()
    {
        var service = Substitute.For<IGitHubOAuthService>();
        service.GetSessionAsync("nope", Arg.Any<CancellationToken>())
            .Returns((OAuthSession?)null);

        await using var factory = CreateFactory(oauthService: service);
        var client = factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        var response = await client.GetAsync("/api/v1/tenant/connectors/github/oauth/session/nope", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    private static WebApplicationFactory<Program> CreateFactory(IGitHubOAuthService? oauthService = null)
    {
        var baseFactory = new CustomWebApplicationFactory();
        return baseFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // Give the OAuth options a real redirect URI so the service
                // constructor is happy — the stub below short-circuits the
                // actual code exchange.
                services.PostConfigure<GitHubOAuthOptions>(opts =>
                {
                    opts.ClientId = "cid";
                    opts.ClientSecret = "csec";
                    opts.RedirectUri = "https://example.com/cb";
                });

                // Drop the stub IConnectorType registered by the shared factory
                // so the real GitHubConnectorType owns the /connectors/github
                // routes.
                var connectorDescriptors = services
                    .Where(d => d.ServiceType == typeof(IConnectorType))
                    .ToList();
                foreach (var d in connectorDescriptors)
                {
                    services.Remove(d);
                }

                // Provide a webhook registrar substitute so GitHubConnectorType
                // construction succeeds.
                var regDescriptors = services
                    .Where(d => d.ServiceType == typeof(IGitHubWebhookRegistrar))
                    .ToList();
                foreach (var d in regDescriptors)
                {
                    services.Remove(d);
                }
                services.AddSingleton(Substitute.For<IGitHubWebhookRegistrar>());

                if (oauthService is not null)
                {
                    var descriptors = services
                        .Where(d => d.ServiceType == typeof(IGitHubOAuthService))
                        .ToList();
                    foreach (var d in descriptors)
                    {
                        services.Remove(d);
                    }
                    services.AddSingleton(oauthService);
                }

                services.AddSingleton<GitHubConnectorType>();
                services.AddSingleton<IConnectorType>(
                    sp => sp.GetRequiredService<GitHubConnectorType>());
            });
        });
    }
}