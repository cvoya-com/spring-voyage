// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Contract;

using System.Net;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Semantic contract tests for the <c>/api/v1/tenant/dashboard/*</c> surface
/// (closes #1255 / C1.3). Validates that every dashboard endpoint response
/// shape matches the committed openapi.json so that required-field drops,
/// status-code reshuffles, and type changes fail CI.
/// </summary>
public class DashboardContractTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public DashboardContractTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetDashboardSummary_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        var entries = new List<DirectoryEntry>
        {
            new(new Address("unit", "contract-unit-dash"), "actor-dash-unit",
                "Contract Unit", "A unit", null, now),
            new(new Address("agent", "contract-agent-dash"), "actor-dash-agent",
                "Contract Agent", "An agent", "backend", now),
        };
        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>()).Returns(entries);

        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Draft);
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(proxy);

        _factory.ActivityQueryService
            .QueryAsync(Arg.Any<ActivityQueryParameters>(), Arg.Any<CancellationToken>())
            .Returns(new ActivityQueryResult(
                Array.Empty<ActivityQueryResult.Item>(), 0, 1, 10));

        _factory.ActivityQueryService
            .GetTotalCostAsync(
                Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(0m);

        var response = await _client.GetAsync("/api/v1/tenant/dashboard/summary", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/dashboard/summary", "get", "200", body);
    }

    [Fact]
    public async Task GetAgentsSummary_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DirectoryEntry>
            {
                new(new Address("agent", "contract-agent-list"), "actor-agent-list",
                    "Contract Agent List", "An agent", "backend", now),
            });

        var response = await _client.GetAsync("/api/v1/tenant/dashboard/agents", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/dashboard/agents", "get", "200", body);
    }

    [Fact]
    public async Task GetUnitsSummary_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<DirectoryEntry>
            {
                new(new Address("unit", "contract-unit-list"), "actor-unit-list",
                    "Contract Unit List", "A unit", null, now),
            });

        var proxy = Substitute.For<IUnitActor>();
        proxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Running);
        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), Arg.Any<string>())
            .Returns(proxy);

        var response = await _client.GetAsync("/api/v1/tenant/dashboard/units", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/dashboard/units", "get", "200", body);
    }

    [Fact]
    public async Task GetCostsSummary_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;

        _factory.ActivityQueryService
            .GetCostBySourceAsync(
                Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(new List<CostBySource>
            {
                new("agent://contract-bot", 1.5m),
            });

        _factory.ActivityQueryService
            .GetTotalCostAsync(
                Arg.Any<string?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(),
                Arg.Any<CancellationToken>())
            .Returns(1.5m);

        var response = await _client.GetAsync("/api/v1/tenant/dashboard/costs", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/dashboard/costs", "get", "200", body);
    }
}