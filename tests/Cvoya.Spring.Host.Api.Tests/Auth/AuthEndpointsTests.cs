// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Auth;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Host.Api.Models;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for the auth token management endpoints.
/// </summary>
public class AuthEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public AuthEndpointsTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreateToken_ReturnsTokenValue()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new CreateTokenRequest("my-token");

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/auth/tokens", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var result = await response.Content.ReadFromJsonAsync<CreateTokenResponse>(ct);
        result.ShouldNotBeNull();
        result!.Name.ShouldBe("my-token");
        result.Token.ShouldNotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ListTokens_ReturnsMetadataWithoutTokenValues()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a token first.
        var createRequest = new CreateTokenRequest("list-test-token", ["read", "write"]);
        var createResponse = await _client.PostAsJsonAsync("/api/v1/tenant/auth/tokens", createRequest, ct);
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        var response = await _client.GetAsync("/api/v1/tenant/auth/tokens", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var tokens = await response.Content.ReadFromJsonAsync<List<TokenResponse>>(ct);
        tokens.ShouldNotBeNull();
        tokens.ShouldContain(t => t.Name == "list-test-token");

        var token = tokens!.First(t => t.Name == "list-test-token");
        token.Scopes.ShouldBe(new[] { "read", "write" }, ignoreOrder: true);
    }

    [Fact]
    public async Task RevokeToken_MarksAsRevoked()
    {
        var ct = TestContext.Current.CancellationToken;

        // Create a token.
        var createRequest = new CreateTokenRequest("revoke-test-token");
        var createResponse = await _client.PostAsJsonAsync("/api/v1/tenant/auth/tokens", createRequest, ct);
        createResponse.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Revoke it.
        var revokeResponse = await _client.DeleteAsync("/api/v1/tenant/auth/tokens/revoke-test-token", ct);
        revokeResponse.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        // Verify it no longer appears in the list.
        var listResponse = await _client.GetAsync("/api/v1/tenant/auth/tokens", ct);
        var tokens = await listResponse.Content.ReadFromJsonAsync<List<TokenResponse>>(ct);
        tokens.ShouldNotBeNull();
        tokens.ShouldNotContain(t => t.Name == "revoke-test-token");
    }

    [Fact]
    public async Task RevokeToken_NotFound_Returns404()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.DeleteAsync("/api/v1/tenant/auth/tokens/nonexistent-token", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

}