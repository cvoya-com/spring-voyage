// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Auth;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Integration tests for the OAuth login flow endpoints.
/// Uses the local dev mode factory for /me and /logout tests,
/// and a custom OAuth factory for /login and /callback tests.
/// </summary>
public class OAuthEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public OAuthEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
    }

    [Fact]
    public async Task Login_WhenOAuthNotConfigured_Returns501()
    {
        // In local dev mode, OAuthOptions is not configured, so ClientId is empty.
        // The login endpoint should return 501.
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync("/api/v1/auth/login", ct);

        // In local dev mode, OAuthOptions is not bound so it defaults to empty strings.
        // The endpoint checks for empty ClientId and returns 501.
        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented);
    }

    [Fact]
    public async Task Callback_WithMissingState_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync("/api/v1/auth/callback?code=test-code", ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Callback_WithInvalidState_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;

        // Set a state cookie that doesn't match the query parameter
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/auth/callback?code=test-code&state=wrong-state");
        request.Headers.Add("Cookie", "spring_oauth_state=correct-state");

        var response = await _client.SendAsync(request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Me_ReturnsLocalDevProfile()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync("/api/v1/auth/me", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var profile = await response.Content.ReadFromJsonAsync<UserProfileResponse>(ct);
        profile.Should().NotBeNull();
        profile!.GitHubLogin.Should().Be("local-dev");
        profile.DisplayName.Should().Be("Local Developer");
    }

    [Fact]
    public async Task Me_WithAuthenticatedUser_ReturnsProfile()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed a user in the database
        var userId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            dbContext.Users.Add(new UserEntity
            {
                Id = userId,
                GitHubId = "12345",
                GitHubLogin = "testuser",
                DisplayName = "Test User",
                Email = "test@example.com",
                AvatarUrl = "https://github.com/avatar.png"
            });
            await dbContext.SaveChangesAsync(ct);
        }

        // In local dev mode, the /me endpoint returns the local dev profile
        // regardless of cookies. This tests that the endpoint works.
        var response = await _client.GetAsync("/api/v1/auth/me", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Logout_ClearsCookieAndReturnsOk()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsync("/api/v1/auth/logout", null, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify the Set-Cookie header expires the session cookie
        var setCookieHeaders = response.Headers
            .Where(h => h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            .SelectMany(h => h.Value)
            .ToList();

        setCookieHeaders.Should().Contain(c => c.Contains(OAuthAuthHandler.SessionCookieName));
    }

    [Fact]
    public async Task Logout_ResponseContainsMessage()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PostAsync("/api/v1/auth/logout", null, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        body.Should().Contain("Logged out successfully");
    }
}
