// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Contract;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Dapr.Costs;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Happy-path contract tests for the two new analytics endpoints (#569 / #570):
/// <list type="bullet">
///   <item><c>GET /api/v1/tenant/analytics/agents/{id}/cost-timeseries</c></item>
///   <item><c>GET /api/v1/tenant/analytics/units/{id}/cost-timeseries</c></item>
///   <item><c>GET /api/v1/tenant/cost/agents/{id}/breakdown</c></item>
/// </list>
/// Each test validates that the response body matches the committed
/// <c>openapi.json</c> contract via <see cref="OpenApiContract.AssertResponse"/>.
/// </summary>
public class AnalyticsContractTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public AnalyticsContractTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    // #569 — agent cost time-series

    [Fact]
    public async Task GetAgentCostTimeseries_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;

        await SeedCostRecordAsync("contract-ts-agent", unitId: null, cost: 0.05m, ct: ct);

        var response = await _client.GetAsync(
            "/api/v1/tenant/analytics/agents/contract-ts-agent/cost-timeseries?window=7d&bucket=1d", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/analytics/agents/{id}/cost-timeseries", "get", "200", body);
    }

    [Fact]
    public async Task GetAgentCostTimeseries_ResponseShape_IsCorrect()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync(
            "/api/v1/tenant/analytics/agents/no-data-agent/cost-timeseries?window=7d&bucket=1d", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<AnalyticsCostTimeseriesResponse>(ct);
        dto.ShouldNotBeNull();
        dto!.Scope.ShouldBe("agents");
        dto.Id.ShouldBe("no-data-agent");
        dto.Bucket.ShouldBe("1d");
        dto.Points.Count.ShouldBe(7);
        dto.Points.ShouldAllBe(p => p.CostUsd >= 0m);
    }

    [Fact]
    public async Task GetAgentCostTimeseries_InvalidBucket_Returns400()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync(
            "/api/v1/tenant/analytics/agents/x/cost-timeseries?bucket=3d", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // #569 — unit cost time-series

    [Fact]
    public async Task GetUnitCostTimeseries_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;

        await SeedCostRecordAsync("contract-ts-agent", unitId: "contract-ts-unit", cost: 0.10m, ct: ct);

        var response = await _client.GetAsync(
            "/api/v1/tenant/analytics/units/contract-ts-unit/cost-timeseries?window=7d&bucket=1d", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/analytics/units/{id}/cost-timeseries", "get", "200", body);
    }

    [Fact]
    public async Task GetUnitCostTimeseries_ResponseShape_IsCorrect()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync(
            "/api/v1/tenant/analytics/units/no-data-unit/cost-timeseries?window=7d&bucket=1d", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<AnalyticsCostTimeseriesResponse>(ct);
        dto.ShouldNotBeNull();
        dto!.Scope.ShouldBe("units");
        dto.Id.ShouldBe("no-data-unit");
        dto.Bucket.ShouldBe("1d");
        dto.Points.Count.ShouldBe(7);
    }

    // #570 — per-agent cost breakdown

    [Fact]
    public async Task GetAgentCostBreakdown_HappyPath_MatchesContract()
    {
        var ct = TestContext.Current.CancellationToken;

        await SeedCostRecordAsync("contract-bd-agent", unitId: null, cost: 0.20m, model: "claude-3-5-sonnet", ct: ct);
        await SeedCostRecordAsync("contract-bd-agent", unitId: null, cost: 0.05m, model: "claude-3-haiku", ct: ct);

        var response = await _client.GetAsync(
            "/api/v1/tenant/cost/agents/contract-bd-agent/breakdown", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync(ct);
        OpenApiContract.AssertResponse(
            "/api/v1/tenant/cost/agents/{id}/breakdown", "get", "200", body);
    }

    [Fact]
    public async Task GetAgentCostBreakdown_ResponseShape_IsCorrect()
    {
        var ct = TestContext.Current.CancellationToken;

        await SeedCostRecordAsync("contract-bd2-agent", unitId: null, cost: 0.15m, model: "gpt-4o", ct: ct);

        var response = await _client.GetAsync(
            "/api/v1/tenant/cost/agents/contract-bd2-agent/breakdown", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<CostBreakdownResponse>(ct);
        dto.ShouldNotBeNull();
        dto!.AgentId.ShouldBe("contract-bd2-agent");
        dto.Entries.ShouldNotBeEmpty();

        var entry = dto.Entries[0];
        entry.Key.ShouldBe("gpt-4o");
        entry.Kind.ShouldBe("model");
        entry.TotalCost.ShouldBeGreaterThan(0m);
        entry.RecordCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task GetAgentCostBreakdown_NoData_ReturnsEmptyEntries()
    {
        var ct = TestContext.Current.CancellationToken;

        var response = await _client.GetAsync(
            "/api/v1/tenant/cost/agents/no-data-bd-agent/breakdown", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var dto = await response.Content.ReadFromJsonAsync<CostBreakdownResponse>(ct);
        dto.ShouldNotBeNull();
        dto!.AgentId.ShouldBe("no-data-bd-agent");
        dto.Entries.ShouldBeEmpty();
    }

    // Helpers

    private async Task SeedCostRecordAsync(
        string agentId,
        string? unitId,
        decimal cost,
        string model = "claude-3-opus",
        CancellationToken ct = default)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.CostRecords.Add(new CostRecord
        {
            Id = Guid.NewGuid(),
            TenantId = "default",
            Model = model,
            Cost = cost,
            InputTokens = 100,
            OutputTokens = 50,
            Timestamp = DateTimeOffset.UtcNow.AddHours(-1),
            Source = CostSource.Work,
        });
        await db.SaveChangesAsync(ct);
    }
}