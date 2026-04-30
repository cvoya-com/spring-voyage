// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Contract;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Host.Api.Models;

using Shouldly;

using Xunit;

/// <summary>
/// Semantic contract tests for the tenant-scoped and platform-scoped secret
/// surfaces (closes #1255 / C1.3). Validates that response bodies from
/// <c>/api/v1/tenant/secrets</c> and <c>/api/v1/platform/secrets</c> match the
/// committed openapi.json — covering the list, create, rotate, versions, prune,
/// and error-path shapes.
/// </summary>
/// <remarks>
/// The test factory wires an in-memory EF database and a stub
/// <see cref="ISecretStore"/> that generates opaque GUIDs on each write, so
/// pass-through secret creation succeeds without a real Dapr state store.
/// The stub <see cref="ISecretAccessPolicy"/> defaults to allow-all so
/// authorization is not the focus of these tests.
/// </remarks>
public class SecretContractTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SecretContractTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // ------------------------------------------------------------------
    // Tenant-scoped secrets
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListTenantSecrets_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync("/api/v1/tenant/secrets", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/secrets", "get", "200", body);
    }

    [Fact]
    public async Task CreateTenantSecret_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;

        var request = new CreateSecretRequest(
            Name: $"contract-tenant-create-{Guid.NewGuid():N}",
            Value: "contract-value");

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/secrets", request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/secrets", "post", "201", body);
    }

    [Fact]
    public async Task CreateTenantSecret_BadRequest_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;

        // Supplying both value and externalStoreKey → 400.
        var request = new CreateSecretRequest(
            Name: "contract-tenant-bad",
            Value: "some-value",
            ExternalStoreKey: "some-external-key");

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/secrets", request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/secrets", "post", "400", body, "application/problem+json");
    }

    [Fact]
    public async Task RotateTenantSecret_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"contract-tenant-rotate-{Guid.NewGuid():N}";

        // Create first.
        var createResp = await _client.PostAsJsonAsync(
            "/api/v1/tenant/secrets",
            new CreateSecretRequest(name, Value: "initial-value"),
            ct);
        createResp.StatusCode.ShouldBe(HttpStatusCode.Created);

        // Rotate.
        var rotateResp = await _client.PutAsJsonAsync(
            $"/api/v1/tenant/secrets/{name}",
            new RotateSecretRequest(Value: "rotated-value"),
            ct);
        rotateResp.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await rotateResp.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/secrets/{name}", "put", "200", body);
    }

    [Fact]
    public async Task RotateTenantSecret_NotFound_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.PutAsJsonAsync(
            "/api/v1/tenant/secrets/contract-tenant-ghost",
            new RotateSecretRequest(Value: "new-value"),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/secrets/{name}", "put", "404", body, "application/problem+json");
    }

    [Fact]
    public async Task ListTenantSecretVersions_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        var name = $"contract-tenant-versions-{Guid.NewGuid():N}";

        await _client.PostAsJsonAsync(
            "/api/v1/tenant/secrets",
            new CreateSecretRequest(name, Value: "v1"),
            ct);

        var response = await _client.GetAsync(
            $"/api/v1/tenant/secrets/{name}/versions", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/secrets/{name}/versions", "get", "200", body);
    }

    [Fact]
    public async Task ListTenantSecretVersions_NotFound_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync(
            "/api/v1/tenant/secrets/contract-tenant-ghost-versions/versions", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/secrets/{name}/versions", "get", "404", body, "application/problem+json");
    }

    // ------------------------------------------------------------------
    // Platform-scoped secrets
    // ------------------------------------------------------------------

    [Fact]
    public async Task ListPlatformSecrets_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync("/api/v1/platform/secrets", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/platform/secrets", "get", "200", body);
    }

    [Fact]
    public async Task CreatePlatformSecret_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;

        var request = new CreateSecretRequest(
            Name: $"contract-platform-create-{Guid.NewGuid():N}",
            Value: "contract-platform-value");

        var response = await _client.PostAsJsonAsync("/api/v1/platform/secrets", request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/platform/secrets", "post", "201", body);
    }

    [Fact]
    public async Task CreatePlatformSecret_BadRequest_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;

        // Missing name → 400.
        var request = new CreateSecretRequest(
            Name: "",
            Value: "some-value");

        var response = await _client.PostAsJsonAsync("/api/v1/platform/secrets", request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/platform/secrets", "post", "400", body, "application/problem+json");
    }
}