// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Costs;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Costs;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Observability;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

public class BudgetEnforcerTests : IDisposable
{
    private static readonly Guid AgentAId = new("aaaaaaaa-1111-1111-1111-000000000001");
    private static readonly string AgentAHex = AgentAId.ToString("N");

    private readonly ActivityEventBus _bus = new();
    private readonly IActivityEventBus _eventBus = Substitute.For<IActivityEventBus>();
    private readonly IStateStore _stateStore = Substitute.For<IStateStore>();
    private readonly ServiceProvider _serviceProvider;
    private readonly string _dbName = $"BudgetEnforcerTests_{Guid.NewGuid()}";

    public BudgetEnforcerTests()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new StaticTenantContext(OssTenantIds.Default));
        services.AddDbContext<SpringDbContext>(o => o
            .UseInMemoryDatabase(_dbName)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));
        _serviceProvider = services.BuildServiceProvider();
    }

    private BudgetEnforcer CreateEnforcer()
    {
        return new BudgetEnforcer(
            _bus,
            _eventBus,
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            _stateStore,
            NullLogger<BudgetEnforcer>.Instance);
    }

    private void SeedAgentBudget(Guid agentId, decimal dailyBudget)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.BudgetLimits.Add(new BudgetLimitEntity
        {
            Id = Guid.NewGuid(),
            TenantId = OssTenantIds.Default,
            ScopeType = BudgetLimitScope.Agent,
            ScopeId = agentId,
            DailyBudget = dailyBudget,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private void SeedTenantBudget(decimal dailyBudget)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.BudgetLimits.Add(new BudgetLimitEntity
        {
            Id = Guid.NewGuid(),
            TenantId = OssTenantIds.Default,
            ScopeType = BudgetLimitScope.Tenant,
            ScopeId = null,
            DailyBudget = dailyBudget,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    private void SeedUnitBudget(Guid unitId, decimal dailyBudget)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.BudgetLimits.Add(new BudgetLimitEntity
        {
            Id = Guid.NewGuid(),
            TenantId = OssTenantIds.Default,
            ScopeType = BudgetLimitScope.Unit,
            ScopeId = unitId,
            DailyBudget = dailyBudget,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
    }

    /// <summary>
    /// Seeds a persisted <see cref="CostRecord"/> so spend queries see prior
    /// ledger history — the enforcer reads spend from <c>cost_records</c>, not
    /// an in-memory accumulator (#3073).
    /// </summary>
    private void SeedCostRecord(Guid agentId, Guid? unitId, decimal cost, DateTimeOffset timestamp)
    {
        using var scope = _serviceProvider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
        db.CostRecords.Add(new CostRecord
        {
            Id = Guid.NewGuid(),
            TenantId = OssTenantIds.Default,
            AgentId = agentId,
            UnitId = unitId,
            Model = "claude-opus-4-8",
            InputTokens = 100,
            OutputTokens = 50,
            Cost = cost,
            Timestamp = timestamp,
            Source = Cvoya.Spring.Core.Costs.CostSource.Work,
        });
        db.SaveChanges();
    }

    private static ActivityEvent CreateCostEvent(Guid agentId, decimal cost, Guid? unitId = null)
    {
        var details = JsonSerializer.SerializeToElement(new
        {
            tenantId = OssTenantIds.Default.ToString("N"),
            unitId = unitId?.ToString("N"),
            model = "claude-3-opus",
            inputTokens = 100,
            outputTokens = 50,
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

    [Fact(Timeout = 10_000)]
    public async Task CheckBudget_UnderThreshold_NoEventEmitted()
    {
        var ct = TestContext.Current.CancellationToken;
        SeedAgentBudget(AgentAId, 10.0m);

        var enforcer = CreateEnforcer();
        await enforcer.StartAsync(ct);

        _bus.Publish(CreateCostEvent(AgentAId, 1.0m)); // 10% of budget
        await Task.Delay(500, ct);

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<ActivityEvent>(),
            Arg.Any<CancellationToken>());

        await enforcer.StopAsync(ct);
        enforcer.Dispose();
    }

    [Fact(Timeout = 10_000)]
    public async Task CheckBudget_AtWarningThreshold_EmitsWarning()
    {
        var ct = TestContext.Current.CancellationToken;
        SeedAgentBudget(AgentAId, 10.0m);

        var enforcer = CreateEnforcer();
        await enforcer.StartAsync(ct);

        _bus.Publish(CreateCostEvent(AgentAId, 8.5m)); // 85% of budget
        await Task.Delay(500, ct);

        await _eventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e => e.Severity == ActivitySeverity.Warning),
            Arg.Any<CancellationToken>());

        await enforcer.StopAsync(ct);
        enforcer.Dispose();
    }

    [Fact(Timeout = 10_000)]
    public async Task CheckBudget_AtErrorThreshold_EmitsError()
    {
        var ct = TestContext.Current.CancellationToken;
        SeedAgentBudget(AgentAId, 10.0m);

        var enforcer = CreateEnforcer();
        await enforcer.StartAsync(ct);

        _bus.Publish(CreateCostEvent(AgentAId, 10.5m)); // 105% of budget
        await Task.Delay(500, ct);

        await _eventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e => e.Severity == ActivitySeverity.Error),
            Arg.Any<CancellationToken>());

        await enforcer.StopAsync(ct);
        enforcer.Dispose();
    }

    [Fact(Timeout = 10_000)]
    public async Task CheckBudget_NoBudgetSet_NoEventEmitted()
    {
        var ct = TestContext.Current.CancellationToken;

        var enforcer = CreateEnforcer();
        await enforcer.StartAsync(ct);

        _bus.Publish(CreateCostEvent(AgentAId, 100.0m));
        await Task.Delay(500, ct);

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<ActivityEvent>(),
            Arg.Any<CancellationToken>());

        await enforcer.StopAsync(ct);
        enforcer.Dispose();
    }

    [Fact(Timeout = 10_000)]
    public async Task CheckBudget_AddsTodaysLedgerSpendToTheCurrentTurn()
    {
        // #3073: spend is the sum of today's cost_records plus the in-flight
        // turn. 5.0m already on the ledger + a 4.0m turn = 9.0m = 90% → warning.
        var ct = TestContext.Current.CancellationToken;
        SeedAgentBudget(AgentAId, 10.0m);
        SeedCostRecord(AgentAId, unitId: null, cost: 5.0m, timestamp: DateTimeOffset.UtcNow);

        var enforcer = CreateEnforcer();
        await enforcer.StartAsync(ct);

        _bus.Publish(CreateCostEvent(AgentAId, 4.0m));
        await Task.Delay(500, ct);

        await _eventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e => e.Severity == ActivitySeverity.Warning),
            Arg.Any<CancellationToken>());

        await enforcer.StopAsync(ct);
        enforcer.Dispose();
    }

    [Fact(Timeout = 10_000)]
    public async Task CheckBudget_DailyReset_YesterdaySpendDoesNotCount()
    {
        // #3073: the budget is DAILY — spend before UTC midnight is outside the
        // window. Yesterday's 9.0m must not bleed into today: a 2.0m turn today
        // is 20% of the budget, so no alert fires. (The pre-#3073 lifetime
        // accumulator would have tripped here.)
        var ct = TestContext.Current.CancellationToken;
        SeedAgentBudget(AgentAId, 10.0m);
        SeedCostRecord(AgentAId, unitId: null, cost: 9.0m, timestamp: DateTimeOffset.UtcNow.AddDays(-1));

        var enforcer = CreateEnforcer();
        await enforcer.StartAsync(ct);

        _bus.Publish(CreateCostEvent(AgentAId, 2.0m));
        await Task.Delay(500, ct);

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<ActivityEvent>(),
            Arg.Any<CancellationToken>());

        await enforcer.StopAsync(ct);
        enforcer.Dispose();
    }

    [Fact(Timeout = 10_000)]
    public async Task CheckBudget_UnitBudgetExceeded_EmitsErrorAndPausesUnit()
    {
        // #3073: unit-scope budgets are enforced symmetrically with agents. The
        // cost event carries the owning unit id; a 10.5m turn against a 10.0m
        // unit budget trips the error + pauses the unit's initiative.
        var ct = TestContext.Current.CancellationToken;
        var unitId = new Guid("bbbbbbbb-2222-2222-2222-000000000002");
        SeedUnitBudget(unitId, 10.0m);

        var enforcer = CreateEnforcer();
        await enforcer.StartAsync(ct);

        _bus.Publish(CreateCostEvent(AgentAId, 10.5m, unitId));
        await Task.Delay(500, ct);

        await _eventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.Severity == ActivitySeverity.Error &&
                e.Source.Scheme == "unit"),
            Arg.Any<CancellationToken>());

        await _stateStore.Received(1).SetAsync(
            $"{unitId:N}:{StateKeys.InitiativeState}",
            Arg.Is<Cvoya.Spring.Dapr.Costs.InitiativePausedState>(s => s.Reason == "BudgetExceeded"),
            Arg.Any<CancellationToken>());

        await enforcer.StopAsync(ct);
        enforcer.Dispose();
    }

    [Fact(Timeout = 10_000)]
    public async Task CheckBudget_AtErrorThreshold_PausesInitiative()
    {
        var ct = TestContext.Current.CancellationToken;
        SeedAgentBudget(AgentAId, 10.0m);

        var enforcer = CreateEnforcer();
        await enforcer.StartAsync(ct);

        _bus.Publish(CreateCostEvent(AgentAId, 10.5m)); // 105% of budget
        await Task.Delay(500, ct);

        await _stateStore.Received(1).SetAsync(
            $"{AgentAHex}:{StateKeys.InitiativeState}",
            Arg.Is<Cvoya.Spring.Dapr.Costs.InitiativePausedState>(s => s.Reason == "BudgetExceeded"),
            Arg.Any<CancellationToken>());

        await enforcer.StopAsync(ct);
        enforcer.Dispose();
    }

    [Fact(Timeout = 10_000)]
    public async Task CheckBudget_TenantBudgetWarning_EmitsWarning()
    {
        var ct = TestContext.Current.CancellationToken;
        SeedTenantBudget(10.0m);

        var enforcer = CreateEnforcer();
        await enforcer.StartAsync(ct);

        _bus.Publish(CreateCostEvent(AgentAId, 8.5m)); // 85% of tenant budget
        await Task.Delay(500, ct);

        await _eventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.Severity == ActivitySeverity.Warning &&
                e.Source.Scheme == "tenant"),
            Arg.Any<CancellationToken>());

        await enforcer.StopAsync(ct);
        enforcer.Dispose();
    }

    [Fact(Timeout = 10_000)]
    public async Task CheckBudget_TenantBudgetExceeded_EmitsError()
    {
        var ct = TestContext.Current.CancellationToken;
        SeedTenantBudget(10.0m);

        var enforcer = CreateEnforcer();
        await enforcer.StartAsync(ct);

        _bus.Publish(CreateCostEvent(AgentAId, 10.5m)); // 105% of tenant budget
        await Task.Delay(500, ct);

        await _eventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.Severity == ActivitySeverity.Error &&
                e.Source.Scheme == "tenant"),
            Arg.Any<CancellationToken>());

        await enforcer.StopAsync(ct);
        enforcer.Dispose();
    }

    [Fact(Timeout = 10_000)]
    public async Task CheckBudget_NoTenantBudget_NoTenantEventEmitted()
    {
        var ct = TestContext.Current.CancellationToken;

        var enforcer = CreateEnforcer();
        await enforcer.StartAsync(ct);

        _bus.Publish(CreateCostEvent(AgentAId, 100.0m));
        await Task.Delay(500, ct);

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Is<ActivityEvent>(e => e.Source.Scheme == "tenant"),
            Arg.Any<CancellationToken>());

        await enforcer.StopAsync(ct);
        enforcer.Dispose();
    }

    public void Dispose()
    {
        _bus.Dispose();
        _serviceProvider.Dispose();
    }
}
