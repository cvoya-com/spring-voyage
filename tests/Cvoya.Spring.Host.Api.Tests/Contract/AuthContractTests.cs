// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Contract;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Host.Api.Models;

using Shouldly;

using Xunit;

/// <summary>
/// Semantic contract tests for the auth surface (#1248 / C1.3). The
/// existing <c>AuthEndpointsTests</c> covers behavioural contract — what
/// the endpoint *does*. These tests cover *wire-shape* contract — that
/// the response body matches the committed openapi.json, including
/// required-property presence and error-envelope shape.
/// </summary>
public class AuthContractTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly HttpClient _client;

    public AuthContractTests(CustomWebApplicationFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task CreateToken_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new CreateTokenRequest("contract-auth-create");

        var response = await _client.PostAsJsonAsync("/api/v1/auth/tokens", request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/auth/tokens", "post", "201", body);
    }

    [Fact]
    public async Task CreateToken_Conflict_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;

        // First create succeeds.
        var first = await _client.PostAsJsonAsync(
            "/api/v1/auth/tokens", new CreateTokenRequest("contract-auth-conflict"), ct);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Second create with the same name → 409 with a problem+json body.
        var second = await _client.PostAsJsonAsync(
            "/api/v1/auth/tokens", new CreateTokenRequest("contract-auth-conflict"), ct);
        second.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var body = await second.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/auth/tokens", "post", "409", body, "application/problem+json");
    }

    [Fact]
    public async Task ListTokens_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed at least one token so the array branch of the schema is exercised.
        await _client.PostAsJsonAsync(
            "/api/v1/auth/tokens", new CreateTokenRequest("contract-auth-list"), ct);

        var response = await _client.GetAsync("/api/v1/auth/tokens", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/auth/tokens", "get", "200", body);
    }

    [Fact]
    public async Task RevokeToken_MissingToken_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.DeleteAsync(
            "/api/v1/auth/tokens/contract-auth-not-there", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/auth/tokens/{name}", "delete", "404", body, "application/problem+json");
    }

    [Fact]
    public async Task GetCurrentUser_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync("/api/v1/auth/me", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/auth/me", "get", "200", body);
    }
}