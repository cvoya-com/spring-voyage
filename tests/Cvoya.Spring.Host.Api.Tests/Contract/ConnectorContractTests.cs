// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Contract;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Host.Api.Models;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Semantic contract tests for the generic connector surface
/// (<c>/api/v1/tenant/connectors/*</c>) (closes #1255 / C1.3). The stub
/// connector type registered by <see cref="CustomWebApplicationFactory"/>
/// drives all tests so coverage stays connector-implementation-agnostic.
/// </summary>
/// <remarks>
/// The install service uses the same in-memory EF database as the rest of
/// the test suite, so <see cref="EnsureStubBoundAsync"/> issues a real
/// <c>POST /bind</c> before assertions that require an installed connector.
/// </remarks>
public class ConnectorContractTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ConnectorContractTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListConnectors_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureStubBoundAsync(ct);

        var response = await _client.GetAsync("/api/v1/tenant/connectors", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/connectors", "get", "200", body);
    }

    [Fact]
    public async Task GetConnector_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureStubBoundAsync(ct);

        var response = await _client.GetAsync("/api/v1/tenant/connectors/stub", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/connectors/{slugOrId}", "get", "200", body);
    }

    [Fact]
    public async Task GetConnector_NotFound_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync(
            "/api/v1/tenant/connectors/contract-connector-ghost", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/connectors/{slugOrId}", "get", "404", body,
            "application/problem+json");
    }

    [Fact]
    public async Task ListConnectorBindings_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        await EnsureStubBoundAsync(ct);

        // Wire up a unit in the directory and a matching store binding so
        // the bindings list includes at least one row.
        var unitId = "contract-bind-unit";
        // Capture TypeId before passing to Returns() — NSubstitute confuses
        // property accesses inside Returns() with substitution setup calls.
        var stubTypeId = _factory.StubConnectorType.TypeId;
        var emptyConfig = System.Text.Json.JsonDocument.Parse("{}").RootElement;

        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DirectoryEntry>
            {
                new(new Address("unit", unitId), $"actor-{unitId}",
                    "Contract Bind Unit", "A unit", null, DateTimeOffset.UtcNow),
            });

        _factory.ConnectorConfigStore.GetAsync(unitId, Arg.Any<CancellationToken>())
            .Returns(new UnitConnectorBinding(stubTypeId, emptyConfig));

        var response = await _client.GetAsync(
            "/api/v1/tenant/connectors/stub/bindings", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/connectors/{slugOrId}/bindings", "get", "200", body);
    }

    [Fact]
    public async Task ListConnectorBindings_UnknownConnector_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync(
            "/api/v1/tenant/connectors/contract-ghost-connector/bindings", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/connectors/{slugOrId}/bindings", "get", "404", body,
            "application/problem+json");
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private async Task EnsureStubBoundAsync(CancellationToken ct)
    {
        // Idempotent — if the stub is already installed this is a no-op
        // (the install service returns the existing row).
        var response = await _client.PostAsJsonAsync(
            "/api/v1/tenant/connectors/stub/bind",
            new ConnectorInstallRequest(null),
            ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}