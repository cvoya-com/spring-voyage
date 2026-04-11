// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Models;

using FluentAssertions;

using NSubstitute;

using Xunit;

public class BudgetEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public BudgetEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task SetAgentBudget_ValidRequest_ReturnsBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new SetBudgetRequest(25.50m);

        var response = await _client.PutAsJsonAsync("/api/v1/agents/budget-agent-1/budget", request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var budget = await response.Content.ReadFromJsonAsync<BudgetResponse>(ct);
        budget.Should().NotBeNull();
        budget!.DailyBudget.Should().Be(25.50m);

        await _factory.StateStore.Received(1).SetAsync(
            $"budget-agent-1:{StateKeys.AgentCostBudget}",
            25.50m,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetAgentBudget_ZeroBudget_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new SetBudgetRequest(0m);

        var response = await _client.PutAsJsonAsync("/api/v1/agents/budget-agent-2/budget", request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetAgentBudget_NegativeBudget_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new SetBudgetRequest(-5m);

        var response = await _client.PutAsJsonAsync("/api/v1/agents/budget-agent-3/budget", request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetAgentBudget_BudgetExists_ReturnsBudget()
    {
        var ct = TestContext.Current.CancellationToken;

        _factory.StateStore.GetAsync<decimal?>(
            $"budget-get-agent:{StateKeys.AgentCostBudget}",
            Arg.Any<CancellationToken>())
            .Returns(10.0m);

        var response = await _client.GetAsync("/api/v1/agents/budget-get-agent/budget", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var budget = await response.Content.ReadFromJsonAsync<BudgetResponse>(ct);
        budget.Should().NotBeNull();
        budget!.DailyBudget.Should().Be(10.0m);
    }

    [Fact]
    public async Task GetAgentBudget_NoBudget_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;

        _factory.StateStore.GetAsync<decimal?>(
            $"no-budget-agent:{StateKeys.AgentCostBudget}",
            Arg.Any<CancellationToken>())
            .Returns((decimal?)null);

        var response = await _client.GetAsync("/api/v1/agents/no-budget-agent/budget", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SetTenantBudget_ValidRequest_ReturnsBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new SetBudgetRequest(100.0m);

        var response = await _client.PutAsJsonAsync("/api/v1/tenant/budget", request, ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var budget = await response.Content.ReadFromJsonAsync<BudgetResponse>(ct);
        budget.Should().NotBeNull();
        budget!.DailyBudget.Should().Be(100.0m);

        await _factory.StateStore.Received().SetAsync(
            $"default:{StateKeys.TenantCostBudget}",
            100.0m,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTenantBudget_BudgetExists_ReturnsBudget()
    {
        var ct = TestContext.Current.CancellationToken;

        _factory.StateStore.GetAsync<decimal?>(
            $"default:{StateKeys.TenantCostBudget}",
            Arg.Any<CancellationToken>())
            .Returns(50.0m);

        var response = await _client.GetAsync("/api/v1/tenant/budget", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var budget = await response.Content.ReadFromJsonAsync<BudgetResponse>(ct);
        budget.Should().NotBeNull();
        budget!.DailyBudget.Should().Be(50.0m);
    }

    [Fact]
    public async Task GetTenantBudget_NoBudget_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;

        _factory.StateStore.GetAsync<decimal?>(
            $"default:{StateKeys.TenantCostBudget}",
            Arg.Any<CancellationToken>())
            .Returns((decimal?)null);

        var response = await _client.GetAsync("/api/v1/tenant/budget", ct);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}