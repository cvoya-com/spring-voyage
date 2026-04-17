// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Models;

using NSubstitute;

using Shouldly;

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

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var budget = await response.Content.ReadFromJsonAsync<BudgetResponse>(ct);
        budget.ShouldNotBeNull();
        budget!.DailyBudget.ShouldBe(25.50m);

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

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task SetAgentBudget_NegativeBudget_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new SetBudgetRequest(-5m);

        var response = await _client.PutAsJsonAsync("/api/v1/agents/budget-agent-3/budget", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
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

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var budget = await response.Content.ReadFromJsonAsync<BudgetResponse>(ct);
        budget.ShouldNotBeNull();
        budget!.DailyBudget.ShouldBe(10.0m);
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

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task SetTenantBudget_ValidRequest_ReturnsBudget()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new SetBudgetRequest(100.0m);

        var response = await _client.PutAsJsonAsync("/api/v1/tenant/budget", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var budget = await response.Content.ReadFromJsonAsync<BudgetResponse>(ct);
        budget.ShouldNotBeNull();
        budget!.DailyBudget.ShouldBe(100.0m);

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

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var budget = await response.Content.ReadFromJsonAsync<BudgetResponse>(ct);
        budget.ShouldNotBeNull();
        budget!.DailyBudget.ShouldBe(50.0m);
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

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // --- PR-C3 / #459: unit budgets ---------------------------------------

    [Fact]
    public async Task SetUnitBudget_ValidRequest_PersistsDailyBudgetUnderUnitKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new SetBudgetRequest(30.00m);

        var response = await _client.PutAsJsonAsync("/api/v1/units/eng-team/budget", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var budget = await response.Content.ReadFromJsonAsync<BudgetResponse>(ct);
        budget.ShouldNotBeNull();
        budget!.DailyBudget.ShouldBe(30.00m);

        await _factory.StateStore.Received(1).SetAsync(
            $"eng-team:{StateKeys.UnitCostBudget}",
            30.00m,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetUnitBudget_ZeroBudget_ReturnsBadRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var request = new SetBudgetRequest(0m);

        var response = await _client.PutAsJsonAsync("/api/v1/units/unit-zero/budget", request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetUnitBudget_BudgetExists_ReturnsBudget()
    {
        var ct = TestContext.Current.CancellationToken;

        _factory.StateStore.GetAsync<decimal?>(
            $"unit-get:{StateKeys.UnitCostBudget}",
            Arg.Any<CancellationToken>())
            .Returns(12.5m);

        var response = await _client.GetAsync("/api/v1/units/unit-get/budget", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var budget = await response.Content.ReadFromJsonAsync<BudgetResponse>(ct);
        budget.ShouldNotBeNull();
        budget!.DailyBudget.ShouldBe(12.5m);
    }

    [Fact]
    public async Task GetUnitBudget_NoBudget_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;

        _factory.StateStore.GetAsync<decimal?>(
            $"unit-missing:{StateKeys.UnitCostBudget}",
            Arg.Any<CancellationToken>())
            .Returns((decimal?)null);

        var response = await _client.GetAsync("/api/v1/units/unit-missing/budget", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}