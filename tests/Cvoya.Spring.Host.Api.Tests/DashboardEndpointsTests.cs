// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Host.Api.Models;

using FluentAssertions;

using NSubstitute;

using Xunit;

public class DashboardEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public DashboardEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetAgentsSummary_ReturnsAgentList()
    {
        var ct = TestContext.Current.CancellationToken;
        var entries = new List<DirectoryEntry>
        {
            new(new Address("agent", "agent-1"), "actor-1", "Agent One", "First agent", "backend", DateTimeOffset.UtcNow),
            new(new Address("unit", "unit-1"), "actor-2", "Unit One", "A unit", null, DateTimeOffset.UtcNow),
            new(new Address("agent", "agent-2"), "actor-3", "Agent Two", "Second agent", "frontend", DateTimeOffset.UtcNow)
        };
        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>()).Returns(entries);

        var response = await _client.GetAsync("/api/v1/dashboard/agents", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var agents = await response.Content.ReadFromJsonAsync<List<AgentDashboardSummary>>(ct);
        agents.Should().HaveCount(2);
        agents![0].Name.Should().Be("agent-1");
        agents[0].DisplayName.Should().Be("Agent One");
        agents[0].Role.Should().Be("backend");
        agents[1].Name.Should().Be("agent-2");
    }

    [Fact]
    public async Task GetUnitsSummary_ReturnsUnitList()
    {
        var ct = TestContext.Current.CancellationToken;
        var entries = new List<DirectoryEntry>
        {
            new(new Address("agent", "agent-1"), "actor-1", "Agent One", "An agent", "backend", DateTimeOffset.UtcNow),
            new(new Address("unit", "unit-1"), "actor-2", "Unit One", "First unit", null, DateTimeOffset.UtcNow),
            new(new Address("unit", "unit-2"), "actor-3", "Unit Two", "Second unit", null, DateTimeOffset.UtcNow)
        };
        _factory.DirectoryService.ListAllAsync(Arg.Any<CancellationToken>()).Returns(entries);

        var response = await _client.GetAsync("/api/v1/dashboard/units", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var units = await response.Content.ReadFromJsonAsync<List<UnitDashboardSummary>>(ct);
        units.Should().HaveCount(2);
        units![0].Name.Should().Be("unit-1");
        units[0].DisplayName.Should().Be("Unit One");
        units[1].Name.Should().Be("unit-2");
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

        var response = await _client.GetAsync("/api/v1/dashboard/costs", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var summary = await response.Content.ReadFromJsonAsync<CostDashboardSummary>(ct);
        summary.Should().NotBeNull();
        summary!.TotalCost.Should().Be(15.75m);
        summary.CostsBySource.Should().HaveCount(2);
    }
}