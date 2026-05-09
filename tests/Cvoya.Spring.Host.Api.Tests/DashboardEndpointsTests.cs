// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;

using NSubstitute;

using Shouldly;

using Xunit;

public class DashboardEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private static readonly Guid Agent_Agent1_Id = new("00000001-1234-5678-9abc-000000000000");
    private static readonly Guid Agent_Agent2_Id = new("00000002-1234-5678-9abc-000000000000");
    private static readonly Guid Unit_Unit1_Id = new("00000003-1234-5678-9abc-000000000000");
    private static readonly Guid Unit_Unit2_Id = new("00000004-1234-5678-9abc-000000000000");
    private static readonly Guid Actor1_Id = new("00000005-1234-5678-9abc-000000000000");
    private static readonly Guid Actor2_Id = new("00000006-1234-5678-9abc-000000000000");
    private static readonly Guid Actor3_Id = new("00000007-1234-5678-9abc-000000000000");

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public DashboardEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetDashboardSummary_ReturnsAggregatedData()
    {
        var ct = TestContext.Current.CancellationToken;

        // Two units (one Running, one Draft) and one agent.
        var entries = new List<DirectoryEntry>
        {
            new(new Address("unit", Unit_Unit1_Id), Actor1_Id, "Unit One", "First unit", null, DateTimeOffset.UtcNow),
            new(new Address("unit", Unit_Unit2_Id), Actor2_Id, "Unit Two", "Second unit", null, DateTimeOffset.UtcNow),
            new(new Address("agent", Agent_Agent1_Id), Actor3_Id, "Agent One", "An agent", "backend", DateTimeOffset.UtcNow),
        };
        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>()).Returns(entries);

        // Unit-1 is Running, Unit-2 defaults to Draft (proxy throws).
        var runningProxy = Substitute.For<IUnitActor>();
        runningProxy.GetStatusAsync(Arg.Any<CancellationToken>()).Returns(UnitStatus.Running);

        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(id => id.GetId() == Actor1_Id.ToString("N")),
                Arg.Any<string>())
            .Returns(runningProxy);

        _factory.ActorProxyFactory
            .CreateActorProxy<IUnitActor>(
                Arg.Is<ActorId>(id => id.GetId() == Actor2_Id.ToString("N")),
                Arg.Any<string>())
            .Returns(_ => throw new Exception("Actor unavailable"));

        // Recent activity.
        var recentItems = new List<ActivityQueryResult.Item>
        {
            new(Guid.NewGuid(), "agent://agent-1", "MessageReceived", "Info", "Agent received message", null, null, DateTimeOffset.UtcNow),
        };
        _factory.ActivityQueryService
            .QueryAsync(Arg.Any<ActivityQueryParameters>(), Arg.Any<CancellationToken>())
            .Returns(new ActivityQueryResult(recentItems, 1, 1, 10));

        // Total cost.
        _factory.ActivityQueryService
            .GetTotalCostAsync(null, null, null, Arg.Any<CancellationToken>())
            .Returns(25.50m);

        var response = await _client.GetAsync("/api/v1/tenant/dashboard/summary", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var summary = await response.Content.ReadFromJsonAsync<DashboardSummary>(JsonOptions, ct);
        summary.ShouldNotBeNull();
        summary!.UnitCount.ShouldBe(2);
        summary.AgentCount.ShouldBe(1);
        summary.TotalCost.ShouldBe(25.50m);
        summary.UnitsByStatus.ShouldContainKeyAndValue(UnitStatus.Running, 1);
        summary.UnitsByStatus.ShouldContainKeyAndValue(UnitStatus.Draft, 1);
        summary.RecentActivity.Count.ShouldBe(1);
        summary.RecentActivity[0].Summary.ShouldBe("Agent received message");

        // Verify inline unit and agent lists.
        summary.Units.Count.ShouldBe(2);
        summary.Units[0].Name.ShouldBe(Unit_Unit1_Id.ToString("N"));
        summary.Units[0].Status.ShouldBe(UnitStatus.Running);
        summary.Units[1].Name.ShouldBe(Unit_Unit2_Id.ToString("N"));
        summary.Units[1].Status.ShouldBe(UnitStatus.Draft);

        summary.Agents.Count.ShouldBe(1);
        summary.Agents[0].Name.ShouldBe(Agent_Agent1_Id.ToString("N"));
        summary.Agents[0].DisplayName.ShouldBe("Agent One");
        summary.Agents[0].Role.ShouldBe("backend");
    }

    [Fact]
    public async Task GetAgentsSummary_ReturnsAgentList()
    {
        var ct = TestContext.Current.CancellationToken;
        var entries = new List<DirectoryEntry>
        {
            new(new Address("agent", Agent_Agent1_Id), Actor1_Id, "Agent One", "First agent", "backend", DateTimeOffset.UtcNow),
            new(new Address("unit", Unit_Unit1_Id), Actor2_Id, "Unit One", "A unit", null, DateTimeOffset.UtcNow),
            new(new Address("agent", Agent_Agent2_Id), Actor3_Id, "Agent Two", "Second agent", "frontend", DateTimeOffset.UtcNow)
        };
        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>()).Returns(entries);

        var response = await _client.GetAsync("/api/v1/tenant/dashboard/agents", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var agents = await response.Content.ReadFromJsonAsync<List<AgentDashboardSummary>>(ct);
        agents!.Count().ShouldBe(2);
        agents![0].Name.ShouldBe(Agent_Agent1_Id.ToString("N"));
        agents[0].DisplayName.ShouldBe("Agent One");
        agents[0].Role.ShouldBe("backend");
        agents[1].Name.ShouldBe(Agent_Agent2_Id.ToString("N"));
    }

    [Fact]
    public async Task GetUnitsSummary_ReturnsUnitList()
    {
        var ct = TestContext.Current.CancellationToken;
        var entries = new List<DirectoryEntry>
        {
            new(new Address("agent", Agent_Agent1_Id), Actor1_Id, "Agent One", "An agent", "backend", DateTimeOffset.UtcNow),
            new(new Address("unit", Unit_Unit1_Id), Actor2_Id, "Unit One", "First unit", null, DateTimeOffset.UtcNow),
            new(new Address("unit", Unit_Unit2_Id), Actor3_Id, "Unit Two", "Second unit", null, DateTimeOffset.UtcNow)
        };
        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>()).Returns(entries);

        var response = await _client.GetAsync("/api/v1/tenant/dashboard/units", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var units = await response.Content.ReadFromJsonAsync<List<UnitDashboardSummary>>(JsonOptions, ct);
        units!.Count().ShouldBe(2);
        units![0].Name.ShouldBe(Unit_Unit1_Id.ToString("N"));
        units[0].DisplayName.ShouldBe("Unit One");
        units[1].Name.ShouldBe(Unit_Unit2_Id.ToString("N"));
    }

    [Fact]
    public async Task GetCostsSummary_ReturnsCostAggregation()
    {
        var ct = TestContext.Current.CancellationToken;
        var costsBySource = new List<CostBySource>
        {
            new("agent://agent-1", 10.5m),
            new("agent://agent-2", 5.25m)
        };
        _factory.ActivityQueryService.GetCostBySourceAsync(Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(costsBySource);
        _factory.ActivityQueryService.GetTotalCostAsync(null, Arg.Any<DateTimeOffset?>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>())
            .Returns(15.75m);

        var response = await _client.GetAsync("/api/v1/tenant/dashboard/costs", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var summary = await response.Content.ReadFromJsonAsync<CostDashboardSummary>(ct);
        summary.ShouldNotBeNull();
        summary!.TotalCost.ShouldBe(15.75m);
        summary.CostsBySource.Count().ShouldBe(2);
    }
}
