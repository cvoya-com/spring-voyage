// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Costs;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Costs;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Observability;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

public class CostTrackerTests : IDisposable
{
    private readonly ActivityEventBus _bus = new();
    private readonly ServiceProvider _serviceProvider;

    public CostTrackerTests()
    {
        var services = new ServiceCollection();
        var dbName = $"CostTrackerTest-{Guid.NewGuid()}";
        services.AddDbContext<SpringDbContext>(options =>
            options.UseInMemoryDatabase(dbName));
        _serviceProvider = services.BuildServiceProvider();
    }

    private CostTracker CreateTracker()
    {
        return new CostTracker(
            _bus,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CostTracker>.Instance);
    }

    private static ActivityEvent CreateCostEvent(
        string agentId = "test-agent",
        decimal cost = 0.05m,
        int inputTokens = 100,
        int outputTokens = 50,
        string model = "claude-3-opus",
        string? costSource = null)
    {
        // The emission site writes costSource as the enum's name; keep the
        // string form here so the test is sensitive to the contract rather
        // than to the internal enum representation.
        var details = JsonSerializer.SerializeToElement(new
        {
            tenantId = "default",
            unitId = "test-unit",
            model,
            inputTokens,
            outputTokens,
            durationMs = 1500.0,
            costSource,
        });

        return new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new Address("agent", agentId),
            ActivityEventType.CostIncurred,
            ActivitySeverity.Info,
            "Cost incurred",
            details,
            Cost: cost);
    }

    [Fact]
    public async Task StartAsync_CostIncurredEvent_PersistsRecord()
    {
        var ct = TestContext.Current.CancellationToken;
        var tracker = CreateTracker();
        await tracker.StartAsync(ct);

        _bus.Publish(CreateCostEvent());

        // Wait for buffer window (1s) + processing time
        await Task.Delay(3000, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var records = await db.CostRecords.ToListAsync(ct);

        records.ShouldHaveSingleItem();
        var record = records[0];
        record.AgentId.ShouldBe("test-agent");
        record.Cost.ShouldBe(0.05m);
        record.InputTokens.ShouldBe(100);
        record.OutputTokens.ShouldBe(50);
        record.Model.ShouldBe("claude-3-opus");
        record.UnitId.ShouldBe("test-unit");
        record.TenantId.ShouldBe("default");
        record.Duration.ShouldNotBeNull();

        await tracker.StopAsync(ct);
        tracker.Dispose();
    }

    /// <summary>
    /// Closes the #391 acceptance: "A cost aggregator subscription computes
    /// per-agent cost from the stream (no ActivityBus polling)." Publishes
    /// three CostIncurred events for two agents via the Rx subject, then
    /// asserts the per-agent totals materialised into <c>CostRecords</c> are
    /// exactly what the stream carried — proving the subscriber, not a
    /// polling loop, is the source of truth for cost aggregation.
    /// </summary>
    [Fact]
    public async Task StartAsync_CostIncurredEventsForMultipleAgents_AggregatesPerAgentFromStream()
    {
        var ct = TestContext.Current.CancellationToken;
        var tracker = CreateTracker();
        await tracker.StartAsync(ct);

        _bus.Publish(CreateCostEvent(agentId: "agent-a", cost: 0.10m));
        _bus.Publish(CreateCostEvent(agentId: "agent-a", cost: 0.05m));
        _bus.Publish(CreateCostEvent(agentId: "agent-b", cost: 0.20m));

        // Wait for buffer window (1s) + processing time.
        await Task.Delay(3000, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var agentA = await db.CostRecords
            .Where(r => r.AgentId == "agent-a")
            .ToListAsync(ct);
        var agentB = await db.CostRecords
            .Where(r => r.AgentId == "agent-b")
            .ToListAsync(ct);

        agentA.Count.ShouldBe(2);
        agentA.Sum(r => r.Cost).ShouldBe(0.15m);
        agentB.ShouldHaveSingleItem().Cost.ShouldBe(0.20m);

        await tracker.StopAsync(ct);
        tracker.Dispose();
    }

    [Fact]
    public async Task StartAsync_NonCostEvent_IsIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        var tracker = CreateTracker();
        await tracker.StartAsync(ct);

        var nonCostEvent = new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new Address("agent", "test"),
            ActivityEventType.MessageReceived,
            ActivitySeverity.Info,
            "Not a cost event");

        _bus.Publish(nonCostEvent);

        await Task.Delay(3000, ct);

        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        var records = await db.CostRecords.ToListAsync(ct);

        records.ShouldBeEmpty();

        await tracker.StopAsync(ct);
        tracker.Dispose();
    }

    [Fact]
    public void MapToRecord_ValidEvent_MapsCorrectly()
    {
        var costEvent = CreateCostEvent(
            agentId: "agent-a",
            cost: 0.10m,
            inputTokens: 200,
            outputTokens: 100,
            model: "claude-3-haiku");

        var record = CostTracker.MapToRecord(costEvent);

        record.ShouldNotBeNull();
        record!.AgentId.ShouldBe("agent-a");
        record.Cost.ShouldBe(0.10m);
        record.InputTokens.ShouldBe(200);
        record.OutputTokens.ShouldBe(100);
        record.Model.ShouldBe("claude-3-haiku");
    }

    [Theory]
    [InlineData("Work", CostSource.Work)]
    [InlineData("work", CostSource.Work)]
    [InlineData("Initiative", CostSource.Initiative)]
    [InlineData("INITIATIVE", CostSource.Initiative)]
    public void MapToRecord_CostSourceString_ParsesCaseInsensitively(string raw, CostSource expected)
    {
        var costEvent = CreateCostEvent(costSource: raw);

        var record = CostTracker.MapToRecord(costEvent);

        record.ShouldNotBeNull();
        record!.Source.ShouldBe(expected);
    }

    [Fact]
    public void MapToRecord_CostSourceMissing_DefaultsToWork()
    {
        var costEvent = CreateCostEvent(costSource: null);

        var record = CostTracker.MapToRecord(costEvent);

        record.ShouldNotBeNull();
        record!.Source.ShouldBe(CostSource.Work);
    }

    [Fact]
    public void MapToRecord_CostSourceUnrecognised_DefaultsToWork()
    {
        // Typo at the emission site must not silently reclassify as Initiative.
        var costEvent = CreateCostEvent(costSource: "proactive");

        var record = CostTracker.MapToRecord(costEvent);

        record.ShouldNotBeNull();
        record!.Source.ShouldBe(CostSource.Work);
    }

    [Fact]
    public void MapToRecord_NullDetails_ReturnsNull()
    {
        var costEvent = new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new Address("agent", "test"),
            ActivityEventType.CostIncurred,
            ActivitySeverity.Info,
            "Cost incurred",
            Cost: 0.05m);

        var record = CostTracker.MapToRecord(costEvent);

        record.ShouldBeNull();
    }

    public void Dispose()
    {
        _bus.Dispose();
        _serviceProvider.Dispose();
    }
}