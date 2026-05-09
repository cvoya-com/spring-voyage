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
    private static readonly Guid TenantA = new("aaaaaaaa-1111-1111-1111-000000000001");
    private static readonly Guid TenantOther = new("aaaaaaaa-1111-1111-1111-000000000002");
    private static readonly Guid AgentAId = new("bbbbbbbb-2222-2222-2222-000000000001");
    private static readonly Guid AgentBId = new("bbbbbbbb-2222-2222-2222-000000000002");
    private static readonly Guid AgentMissingId = new("bbbbbbbb-2222-2222-2222-000000000003");
    private static readonly Guid UnitAId = new("cccccccc-3333-3333-3333-000000000001");
    private static readonly Guid UnitXId = new("cccccccc-3333-3333-3333-000000000002");
    private static readonly Guid UnitYId = new("cccccccc-3333-3333-3333-000000000003");

    private readonly SpringDbContext _dbContext;

    public CostAggregationTests()
    {
        var options = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase($"CostAggregationTest-{Guid.NewGuid()}")
            .Options;
        // Tests seed rows with TenantId = TenantA; align the
        // DbContext-level tenant filter so those rows are visible.
        _dbContext = new SpringDbContext(options, new StaticTenantContext(TenantA));
    }

    private CostAggregation CreateService() => new(_dbContext);

    private static CostRecord CreateRecord(
        Guid? agentId = null,
        Guid? unitId = null,
        Guid? tenantId = null,
        decimal cost = 0.05m,
        int inputTokens = 100,
        int outputTokens = 50,
        DateTimeOffset? timestamp = null,
        CostSource source = CostSource.Work)
    {
        return new CostRecord
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId ?? TenantA,
            AgentId = agentId ?? AgentAId,
            UnitId = unitId ?? UnitAId,
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
            CreateRecord(agentId: AgentAId, cost: 0.10m, inputTokens: 200, outputTokens: 100, timestamp: now),
            CreateRecord(agentId: AgentAId, cost: 0.20m, inputTokens: 300, outputTokens: 150, timestamp: now),
            CreateRecord(agentId: AgentBId, cost: 0.05m, timestamp: now)); // different agent
        await _dbContext.SaveChangesAsync(ct);

        var service = CreateService();
        var result = await service.GetAgentCostAsync(AgentAId, now.AddHours(-1), now.AddHours(1), ct);

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
        var result = await service.GetAgentCostAsync(AgentMissingId, now.AddHours(-1), now.AddHours(1), ct);

        result.TotalCost.ShouldBe(0m);
        result.RecordCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetUnitCostAsync_WithRecords_ReturnsAggregation()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        _dbContext.CostRecords.AddRange(
            CreateRecord(unitId: UnitXId, cost: 0.15m, timestamp: now),
            CreateRecord(unitId: UnitXId, cost: 0.25m, timestamp: now),
            CreateRecord(unitId: UnitYId, cost: 0.10m, timestamp: now)); // different unit
        await _dbContext.SaveChangesAsync(ct);

        var service = CreateService();
        var result = await service.GetUnitCostAsync(UnitXId, now.AddHours(-1), now.AddHours(1), ct);

        result.TotalCost.ShouldBe(0.40m);
        result.RecordCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetTenantCostAsync_WithRecords_ReturnsAggregation()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        // The DbContext-level tenant filter scopes all reads to the
        // ambient tenant (TenantA for this test fixture). Rows
        // written for other tenants (TenantOther) are invisible on read —
        // which is the guarantee the whole scoping work introduces.
        // The test continues to verify that GetTenantCostAsync narrows
        // to the requested tenant AND that cross-tenant rows are
        // filtered out, so rows for a different tenant must not count.
        _dbContext.CostRecords.AddRange(
            CreateRecord(tenantId: TenantA, cost: 0.50m, timestamp: now),
            CreateRecord(tenantId: TenantA, cost: 0.30m, timestamp: now),
            CreateRecord(tenantId: TenantOther, cost: 0.10m, timestamp: now)); // different tenant
        await _dbContext.SaveChangesAsync(ct);

        var service = CreateService();
        var result = await service.GetTenantCostAsync(TenantA, now.AddHours(-1), now.AddHours(1), ct);

        result.TotalCost.ShouldBe(0.80m);
        result.RecordCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetAgentCostAsync_SplitsCostBySource()
    {
        var ct = TestContext.Current.CancellationToken;
        var now = DateTimeOffset.UtcNow;
        _dbContext.CostRecords.AddRange(
            CreateRecord(agentId: AgentAId, cost: 0.10m, timestamp: now, source: CostSource.Work),
            CreateRecord(agentId: AgentAId, cost: 0.07m, timestamp: now, source: CostSource.Work),
            CreateRecord(agentId: AgentAId, cost: 0.03m, timestamp: now, source: CostSource.Initiative));
        await _dbContext.SaveChangesAsync(ct);

        var service = CreateService();
        var result = await service.GetAgentCostAsync(AgentAId, now.AddHours(-1), now.AddHours(1), ct);

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
            CreateRecord(agentId: AgentAId, cost: 0.05m, timestamp: now, source: CostSource.Work));
        await _dbContext.SaveChangesAsync(ct);

        var service = CreateService();
        var result = await service.GetAgentCostAsync(AgentAId, now.AddHours(-1), now.AddHours(1), ct);

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
        var result = await service.GetAgentCostAsync(AgentAId, now.AddHours(-1), now.AddHours(1), ct);

        result.TotalCost.ShouldBe(0.20m);
        result.RecordCount.ShouldBe(1);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
    }
}
