// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Contract;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Semantic contract tests for the <c>/api/v1/units</c> CRUD + lifecycle
/// surface (#1248 / C1.3). Validates response bodies against the committed
/// openapi.json so semantic drift (required→optional, status-code shuffle,
/// problem+json shape change) fails CI.
/// </summary>
public class UnitContractTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public UnitContractTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task ListUnits_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        ResetDirectory();
        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DirectoryEntry>
            {
                new(new Address("unit", "contract-list-unit"),
                    "actor-list-unit",
                    "Contract List Unit",
                    "A unit for contract tests",
                    null,
                    DateTimeOffset.UtcNow),
            });

        // The list endpoint reads each member-actor's status as Draft when
        // the proxy resolves. Pin the actor proxy so the status read is
        // deterministic.
        ArrangeUnitActor(UnitStatus.Draft);

        var response = await _client.GetAsync("/api/v1/tenant/units", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/units", "get", "200", body);
    }

    [Fact]
    public async Task CreateUnit_TopLevel_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        ResetDirectory();
        ArrangeUnitActor(UnitStatus.Draft);

        // Top-level unit creation does not need a parent unit to resolve;
        // the unit doesn't have to pre-exist.
        _factory.DirectoryService
            .ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);
        _factory.DirectoryService
            .RegisterAsync(Arg.Any<DirectoryEntry>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var request = new CreateUnitRequest(
            Name: "contract-create-unit",
            DisplayName: "Contract Create Unit",
            Description: "A unit for contract tests",
            IsTopLevel: true);

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/units", request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Created);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse("/api/v1/tenant/units", "post", "201", body);
    }

    [Fact]
    public async Task CreateUnit_NeitherParentNorTopLevel_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;
        ResetDirectory();

        var request = new CreateUnitRequest(
            Name: "contract-orphan",
            DisplayName: "Contract Orphan",
            Description: "Caller forgot the parent",
            IsTopLevel: null,
            ParentUnitIds: null);

        var response = await _client.PostAsJsonAsync("/api/v1/tenant/units", request, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/units", "post", "400", body, "application/problem+json");
    }

    [Fact]
    public async Task GetUnit_NotFound_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;
        ResetDirectory();
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == "contract-ghost-unit"),
                Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync("/api/v1/tenant/units/contract-ghost-unit", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/units/{id}", "get", "404", body, "application/problem+json");
    }

    [Fact]
    public async Task GetUnitReadiness_NotFound_MatchesProblemDetailsContract()
    {
        var ct = TestContext.Current.CancellationToken;
        ResetDirectory();
        _factory.DirectoryService
            .ResolveAsync(
                Arg.Is<Address>(a => a.Scheme == "unit" && a.Path == "contract-ghost-readiness"),
                Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var response = await _client.GetAsync(
            "/api/v1/tenant/units/contract-ghost-readiness/readiness", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/units/{id}/readiness", "get", "404", body, "application/problem+json");
    }

    private void ResetDirectory()
    {
        _factory.DirectoryService.ClearReceivedCalls();
        _factory.ActorProxyFactory.ClearReceivedCalls();
    }

    private void ArrangeUnitActor(UnitStatus status)
    {
        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(status);
        proxy.GetMetadataAsync(Arg.Any<CancellationToken>())
            .Returns(new UnitMetadata(null, null, null, null));
        proxy.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Address>());
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(proxy);
    }
}