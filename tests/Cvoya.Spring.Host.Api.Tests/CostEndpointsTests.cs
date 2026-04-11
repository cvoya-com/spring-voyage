// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests;

using System.Net;
using System.Net.Http.Json;

using Cvoya.Spring.Dapr.Costs;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Host.Api.Models;

using FluentAssertions;

using Microsoft.Extensions.DependencyInjection;

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
            CreateRecord("cost-agent-1", "unit-1", "tenant-1", 0.10m, 200, 100, now),
            CreateRecord("cost-agent-1", "unit-1", "tenant-1", 0.20m, 300, 150, now));
        await db.SaveChangesAsync(ct);

        var from = Uri.EscapeDataString(now.AddHours(-1).ToString("O"));
        var to = Uri.EscapeDataString(now.AddHours(1).ToString("O"));
        var response = await _client.GetAsync(
            $"/api/v1/costs/agents/cost-agent-1?from={from}&to={to}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var summary = await response.Content.ReadFromJsonAsync<CostSummaryResponse>(ct);
        summary.Should().NotBeNull();
        summary!.TotalCost.Should().Be(0.30m);
        summary.TotalInputTokens.Should().Be(500);
        summary.TotalOutputTokens.Should().Be(250);
        summary.RecordCount.Should().Be(2);
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

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var summary = await response.Content.ReadFromJsonAsync<CostSummaryResponse>(ct);
        summary.Should().NotBeNull();
        summary!.TotalCost.Should().Be(0m);
        summary.RecordCount.Should().Be(0);
    }

    [Fact]
    public async Task GetUnitCost_WithRecords_ReturnsSummary()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.CostRecords.AddRange(
            CreateRecord("agent-x", "cost-unit-1", "tenant-1", 0.15m, 100, 50, now),
            CreateRecord("agent-y", "cost-unit-1", "tenant-1", 0.25m, 200, 100, now));
        await db.SaveChangesAsync(ct);

        var from = Uri.EscapeDataString(now.AddHours(-1).ToString("O"));
        var to = Uri.EscapeDataString(now.AddHours(1).ToString("O"));
        var response = await _client.GetAsync(
            $"/api/v1/costs/units/cost-unit-1?from={from}&to={to}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var summary = await response.Content.ReadFromJsonAsync<CostSummaryResponse>(ct);
        summary.Should().NotBeNull();
        summary!.TotalCost.Should().Be(0.40m);
        summary.RecordCount.Should().Be(2);
    }

    [Fact]
    public async Task GetTenantCost_WithRecords_ReturnsSummary()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.CostRecords.AddRange(
            CreateRecord("agent-a", "unit-a", "cost-tenant-1", 0.50m, 500, 250, now),
            CreateRecord("agent-b", "unit-b", "cost-tenant-1", 0.30m, 300, 150, now));
        await db.SaveChangesAsync(ct);

        var from = Uri.EscapeDataString(now.AddHours(-1).ToString("O"));
        var to = Uri.EscapeDataString(now.AddHours(1).ToString("O"));
        var response = await _client.GetAsync(
            $"/api/v1/costs/tenant?tenantId=cost-tenant-1&from={from}&to={to}", ct);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var summary = await response.Content.ReadFromJsonAsync<CostSummaryResponse>(ct);
        summary.Should().NotBeNull();
        summary!.TotalCost.Should().Be(0.80m);
        summary.RecordCount.Should().Be(2);
    }

    private static CostRecord CreateRecord(
        string agentId,
        string unitId,
        string tenantId,
        decimal cost,
        int inputTokens,
        int outputTokens,
        DateTimeOffset timestamp)
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
        };
    }
}