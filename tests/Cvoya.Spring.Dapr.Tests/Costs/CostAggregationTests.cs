// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Costs;

using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Dapr.Costs;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;

using Shouldly;

using Xunit;

public class CostAggregationTests : IDisposable
{
    private readonly SpringDbContext _dbContext;

    public CostAggregationTests()
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase($"CostAggregationTest-{Guid.NewGuid()}")
            .Options;
        // Tests seed rows with TenantId = "tenant-a"; align the
        // DbContext-level tenant filter so those rows are visible.
        _dbContext = new SpringDbContext(options, new StaticTenantContext("tenant-a"));
    }

    private CostAggregation CreateService() => new(_dbContext);

    private CostRecord CreateRecord(
        string agentId = "agent-a",
        string? unitId = "unit-a",
        string tenantId = "tenant-a",
        decimal cost = 0.05m,
        int inputTokens = 100,
        int outputTokens = 50,
        DateTimeOffset? timestamp = null,
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
            Timestamp = timestamp ?? DateTimeOffset.UtcNow,
            Source = source,
        };
    }

    [Fact]
    public async Task GetAgentCostAsync_WithRecords_ReturnsAggregation()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        _dbContext.CostRecords.AddRange(
            CreateRecord(agentId: "agent-a", cost: 0.10m, inputTokens: 200, outputTokens: 100, timestamp: now),
            CreateRecord(agentId: "agent-a", cost: 0.20m, inputTokens: 300, outputTokens: 150, timestamp: now),
            CreateRecord(agentId: "agent-b", cost: 0.05m, timestamp: now)); // different agent
        await _dbContext.SaveChangesAsync(ct);

        var service = CreateService();
        var result = await service.GetAgentCostAsync("agent-a", now.AddHours(-1), now.AddHours(1), ct);

        result.TotalCost.ShouldBe(0.30m);
        result.TotalInputTokens.ShouldBe(500);
        result.TotalOutputTokens.ShouldBe(250);
        result.RecordCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetAgentCostAsync_NoRecords_ReturnsZero()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;

        var service = CreateService();
        var result = await service.GetAgentCostAsync("nonexistent", now.AddHours(-1), now.AddHours(1), ct);

        result.TotalCost.ShouldBe(0m);
        result.RecordCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetUnitCostAsync_WithRecords_ReturnsAggregation()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        _dbContext.CostRecords.AddRange(
            CreateRecord(unitId: "unit-x", cost: 0.15m, timestamp: now),
            CreateRecord(unitId: "unit-x", cost: 0.25m, timestamp: now),
            CreateRecord(unitId: "unit-y", cost: 0.10m, timestamp: now)); // different unit
        await _dbContext.SaveChangesAsync(ct);

        var service = CreateService();
        var result = await service.GetUnitCostAsync("unit-x", now.AddHours(-1), now.AddHours(1), ct);

        result.TotalCost.ShouldBe(0.40m);
        result.RecordCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetTenantCostAsync_WithRecords_ReturnsAggregation()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        // The DbContext-level tenant filter scopes all reads to the
        // ambient tenant ("tenant-a" for this test fixture). Rows
        // written for other tenants ("other") are invisible on read —
        // which is the guarantee the whole scoping work introduces.
        // The test continues to verify that GetTenantCostAsync narrows
        // to the requested tenant AND that cross-tenant rows are
        // filtered out, so rows for a different tenant must not count.
        _dbContext.CostRecords.AddRange(
            CreateRecord(tenantId: "tenant-a", cost: 0.50m, timestamp: now),
            CreateRecord(tenantId: "tenant-a", cost: 0.30m, timestamp: now),
            CreateRecord(tenantId: "other", cost: 0.10m, timestamp: now)); // different tenant
        await _dbContext.SaveChangesAsync(ct);

        var service = CreateService();
        var result = await service.GetTenantCostAsync("tenant-a", now.AddHours(-1), now.AddHours(1), ct);

        result.TotalCost.ShouldBe(0.80m);
        result.RecordCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetAgentCostAsync_SplitsCostBySource()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        _dbContext.CostRecords.AddRange(
            CreateRecord(agentId: "agent-a", cost: 0.10m, timestamp: now, source: CostSource.Work),
            CreateRecord(agentId: "agent-a", cost: 0.07m, timestamp: now, source: CostSource.Work),
            CreateRecord(agentId: "agent-a", cost: 0.03m, timestamp: now, source: CostSource.Initiative));
        await _dbContext.SaveChangesAsync(ct);

        var service = CreateService();
        var result = await service.GetAgentCostAsync("agent-a", now.AddHours(-1), now.AddHours(1), ct);

        result.TotalCost.ShouldBe(0.20m);
        result.WorkCost.ShouldBe(0.17m);
        result.InitiativeCost.ShouldBe(0.03m);
        // The two sub-totals must add up to the total — keep the invariant explicit
        // so a future schema change (adding a third source) can't silently drop
        // cost from the reported split.
        (result.WorkCost + result.InitiativeCost).ShouldBe(result.TotalCost);
    }

    [Fact]
    public async Task GetAgentCostAsync_NoInitiativeRecords_InitiativeCostIsZero()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        _dbContext.CostRecords.Add(
            CreateRecord(agentId: "agent-a", cost: 0.05m, timestamp: now, source: CostSource.Work));
        await _dbContext.SaveChangesAsync(ct);

        var service = CreateService();
        var result = await service.GetAgentCostAsync("agent-a", now.AddHours(-1), now.AddHours(1), ct);

        result.TotalCost.ShouldBe(0.05m);
        result.WorkCost.ShouldBe(0.05m);
        result.InitiativeCost.ShouldBe(0m);
    }

    [Fact]
    public async Task GetAgentCostAsync_OutOfRange_ExcludesRecords()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        _dbContext.CostRecords.AddRange(
            CreateRecord(cost: 0.10m, timestamp: now.AddDays(-10)),
            CreateRecord(cost: 0.20m, timestamp: now));
        await _dbContext.SaveChangesAsync(ct);

        var service = CreateService();
        var result = await service.GetAgentCostAsync("agent-a", now.AddHours(-1), now.AddHours(1), ct);

        result.TotalCost.ShouldBe(0.20m);
        result.RecordCount.ShouldBe(1);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }
}