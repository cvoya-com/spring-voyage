// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Dapr.Costs;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Host.Api.Models;

using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

public class CostEndpointsTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public CostEndpointsTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task GetAgentCost_WithRecords_ReturnsSummary()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.CostRecords.AddRange(
            CreateRecord("cost-agent-1", "unit-1", "default", 0.10m, 200, 100, now),
            CreateRecord("cost-agent-1", "unit-1", "default", 0.20m, 300, 150, now));
        await db.SaveChangesAsync(ct);

        var from = Uri.EscapeDataString(now.AddHours(-1).ToString("O"));
        var to = Uri.EscapeDataString(now.AddHours(1).ToString("O"));
        var response = await _client.GetAsync(
            $"/api/v1/costs/agents/cost-agent-1?from={from}&to={to}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var summary = await response.Content.ReadFromJsonAsync<CostSummaryResponse>(ct);
        summary.ShouldNotBeNull();
        summary!.TotalCost.ShouldBe(0.30m);
        summary.TotalInputTokens.ShouldBe(500);
        summary.TotalOutputTokens.ShouldBe(250);
        summary.RecordCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetAgentCost_NoRecords_ReturnsZero()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        var from = Uri.EscapeDataString(now.AddHours(-1).ToString("O"));
        var to = Uri.EscapeDataString(now.AddHours(1).ToString("O"));
        var response = await _client.GetAsync(
            $"/api/v1/costs/agents/nonexistent-agent?from={from}&to={to}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var summary = await response.Content.ReadFromJsonAsync<CostSummaryResponse>(ct);
        summary.ShouldNotBeNull();
        summary!.TotalCost.ShouldBe(0m);
        summary.RecordCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetAgentCost_ReturnsWorkInitiativeSplit()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.CostRecords.AddRange(
            CreateRecord("split-agent", "unit-1", "default", 0.08m, 100, 50, now, CostSource.Work),
            CreateRecord("split-agent", "unit-1", "default", 0.04m, 100, 50, now, CostSource.Work),
            CreateRecord("split-agent", "unit-1", "default", 0.03m, 100, 50, now, CostSource.Initiative));
        await db.SaveChangesAsync(ct);

        var from = Uri.EscapeDataString(now.AddHours(-1).ToString("O"));
        var to = Uri.EscapeDataString(now.AddHours(1).ToString("O"));
        var response = await _client.GetAsync(
            $"/api/v1/costs/agents/split-agent?from={from}&to={to}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var summary = await response.Content.ReadFromJsonAsync<CostSummaryResponse>(ct);
        summary.ShouldNotBeNull();
        summary!.TotalCost.ShouldBe(0.15m);
        summary.WorkCost.ShouldBe(0.12m);
        summary.InitiativeCost.ShouldBe(0.03m);
    }

    [Fact]
    public async Task GetUnitCost_WithRecords_ReturnsSummary()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.CostRecords.AddRange(
            CreateRecord("agent-x", "cost-unit-1", "default", 0.15m, 100, 50, now),
            CreateRecord("agent-y", "cost-unit-1", "default", 0.25m, 200, 100, now));
        await db.SaveChangesAsync(ct);

        var from = Uri.EscapeDataString(now.AddHours(-1).ToString("O"));
        var to = Uri.EscapeDataString(now.AddHours(1).ToString("O"));
        var response = await _client.GetAsync(
            $"/api/v1/costs/units/cost-unit-1?from={from}&to={to}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var summary = await response.Content.ReadFromJsonAsync<CostSummaryResponse>(ct);
        summary.ShouldNotBeNull();
        summary!.TotalCost.ShouldBe(0.40m);
        summary.RecordCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetTenantCost_WithRecords_ReturnsSummary()
    {
        var ct = TestContext.Current.CancellationToken;
        // Use a time window far enough in the past that other tests
        // running against the shared integration fixture don't land
        // records inside it. GetTenantCostAsync aggregates every cost
        // row whose timestamp falls in the range — unlike the
        // agent/unit variants, there is no other natural shard to
        // isolate the rows. Pinning the window to a historical date
        // keeps this test deterministic without needing per-test
        // tenants (which would need a per-test tenant-context swap).
        var testWindow = new DateTimeOffset(2020, 1, 15, 12, 0, 0, TimeSpan.Zero);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.CostRecords.AddRange(
            CreateRecord("agent-a", "unit-a", "default", 0.50m, 500, 250, testWindow),
            CreateRecord("agent-b", "unit-b", "default", 0.30m, 300, 150, testWindow));
        await db.SaveChangesAsync(ct);

        var from = Uri.EscapeDataString(testWindow.AddHours(-1).ToString("O"));
        var to = Uri.EscapeDataString(testWindow.AddHours(1).ToString("O"));
        var response = await _client.GetAsync(
            $"/api/v1/costs/tenant?tenantId=default&from={from}&to={to}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var summary = await response.Content.ReadFromJsonAsync<CostSummaryResponse>(ct);
        summary.ShouldNotBeNull();
        summary!.TotalCost.ShouldBe(0.80m);
        summary.RecordCount.ShouldBe(2);
    }

    private static CostRecord CreateRecord(
        string agentId,
        string unitId,
        string tenantId,
        decimal cost,
        int inputTokens,
        int outputTokens,
        DateTimeOffset timestamp,
        CostSource source = CostSource.Work)
    {
        return new CostRecord
        {
            Id = Guid.NewGuid(),
            AgentId = agentId,
            UnitId = unitId,
            TenantId = tenantId,
            Model = "claude-3-opus",
            Cost = cost,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            Timestamp = timestamp,
            Source = source,
        };
    }
}