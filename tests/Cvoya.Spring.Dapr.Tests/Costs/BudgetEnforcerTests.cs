// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Costs;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Costs;
using Cvoya.Spring.Dapr.Observability;

using FluentAssertions;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Xunit;

public class BudgetEnforcerTests : IDisposable
{
    private readonly ActivityEventBus _bus = new();
    private readonly IActivityEventBus _eventBus = Substitute.For<IActivityEventBus>();
    private readonly IStateStore _stateStore = Substitute.For<IStateStore>();

    private BudgetEnforcer CreateEnforcer()
    {
        return new BudgetEnforcer(
            _bus,
            _eventBus,
            _stateStore,
            NullLogger<BudgetEnforcer>.Instance);
    }

    private static ActivityEvent CreateCostEvent(string agentId, decimal cost)
    {
        var details = JsonSerializer.SerializeToElement(new
        {
            tenantId = "default",
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

    [Fact]
    public async Task CheckBudget_UnderThreshold_NoEventEmitted()
    {
        var ct = TestContext.Current.CancellationToken;
        _stateStore.GetAsync<decimal?>($"agent-a:{StateKeys.AgentCostBudget}", Arg.Any<CancellationToken>())
            .Returns(10.0m);

        var enforcer = CreateEnforcer();
        await enforcer.StartAsync(ct);

        _bus.Publish(CreateCostEvent("agent-a", 1.0m)); // 10% of budget
        await Task.Delay(500, ct);

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<ActivityEvent>(),
            Arg.Any<CancellationToken>());

        await enforcer.StopAsync(ct);
        enforcer.Dispose();
    }

    [Fact]
    public async Task CheckBudget_AtWarningThreshold_EmitsWarning()
    {
        var ct = TestContext.Current.CancellationToken;
        _stateStore.GetAsync<decimal?>($"agent-a:{StateKeys.AgentCostBudget}", Arg.Any<CancellationToken>())
            .Returns(10.0m);

        var enforcer = CreateEnforcer();
        await enforcer.StartAsync(ct);

        _bus.Publish(CreateCostEvent("agent-a", 8.5m)); // 85% of budget
        await Task.Delay(500, ct);

        await _eventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e => e.Severity == ActivitySeverity.Warning),
            Arg.Any<CancellationToken>());

        await enforcer.StopAsync(ct);
        enforcer.Dispose();
    }

    [Fact]
    public async Task CheckBudget_AtErrorThreshold_EmitsError()
    {
        var ct = TestContext.Current.CancellationToken;
        _stateStore.GetAsync<decimal?>($"agent-a:{StateKeys.AgentCostBudget}", Arg.Any<CancellationToken>())
            .Returns(10.0m);

        var enforcer = CreateEnforcer();
        await enforcer.StartAsync(ct);

        _bus.Publish(CreateCostEvent("agent-a", 10.5m)); // 105% of budget
        await Task.Delay(500, ct);

        await _eventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e => e.Severity == ActivitySeverity.Error),
            Arg.Any<CancellationToken>());

        await enforcer.StopAsync(ct);
        enforcer.Dispose();
    }

    [Fact]
    public async Task CheckBudget_NoBudgetSet_NoEventEmitted()
    {
        var ct = TestContext.Current.CancellationToken;
        _stateStore.GetAsync<decimal?>($"agent-a:{StateKeys.AgentCostBudget}", Arg.Any<CancellationToken>())
            .Returns((decimal?)null);

        var enforcer = CreateEnforcer();
        await enforcer.StartAsync(ct);

        _bus.Publish(CreateCostEvent("agent-a", 100.0m));
        await Task.Delay(500, ct);

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<ActivityEvent>(),
            Arg.Any<CancellationToken>());

        await enforcer.StopAsync(ct);
        enforcer.Dispose();
    }

    [Fact]
    public async Task CheckBudget_AccumulatesMultipleEvents()
    {
        var ct = TestContext.Current.CancellationToken;
        _stateStore.GetAsync<decimal?>($"agent-a:{StateKeys.AgentCostBudget}", Arg.Any<CancellationToken>())
            .Returns(10.0m);

        var enforcer = CreateEnforcer();
        await enforcer.StartAsync(ct);

        // First event: 5.0m (50% - no alert)
        _bus.Publish(CreateCostEvent("agent-a", 5.0m));
        await Task.Delay(500, ct);

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<ActivityEvent>(),
            Arg.Any<CancellationToken>());

        // Second event: 4.0m (total 9.0m = 90% - warning)
        _bus.Publish(CreateCostEvent("agent-a", 4.0m));
        await Task.Delay(500, ct);

        await _eventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e => e.Severity == ActivitySeverity.Warning),
            Arg.Any<CancellationToken>());

        await enforcer.StopAsync(ct);
        enforcer.Dispose();
    }

    [Fact]
    public async Task CheckBudget_AtErrorThreshold_PausesInitiative()
    {
        var ct = TestContext.Current.CancellationToken;
        _stateStore.GetAsync<decimal?>($"agent-a:{StateKeys.AgentCostBudget}", Arg.Any<CancellationToken>())
            .Returns(10.0m);

        var enforcer = CreateEnforcer();
        await enforcer.StartAsync(ct);

        _bus.Publish(CreateCostEvent("agent-a", 10.5m)); // 105% of budget
        await Task.Delay(500, ct);

        await _stateStore.Received(1).SetAsync(
            $"agent-a:{StateKeys.InitiativeState}",
            Arg.Is<Cvoya.Spring.Dapr.Costs.InitiativePausedState>(s => s.Reason == "BudgetExceeded"),
            Arg.Any<CancellationToken>());

        await enforcer.StopAsync(ct);
        enforcer.Dispose();
    }

    [Fact]
    public async Task CheckBudget_TenantBudgetWarning_EmitsWarning()
    {
        var ct = TestContext.Current.CancellationToken;
        _stateStore.GetAsync<decimal?>($"default:{StateKeys.TenantCostBudget}", Arg.Any<CancellationToken>())
            .Returns(10.0m);

        var enforcer = CreateEnforcer();
        await enforcer.StartAsync(ct);

        _bus.Publish(CreateCostEvent("agent-a", 8.5m)); // 85% of tenant budget
        await Task.Delay(500, ct);

        await _eventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.Severity == ActivitySeverity.Warning &&
                e.Source.Scheme == "tenant"),
            Arg.Any<CancellationToken>());

        await enforcer.StopAsync(ct);
        enforcer.Dispose();
    }

    [Fact]
    public async Task CheckBudget_TenantBudgetExceeded_EmitsError()
    {
        var ct = TestContext.Current.CancellationToken;
        _stateStore.GetAsync<decimal?>($"default:{StateKeys.TenantCostBudget}", Arg.Any<CancellationToken>())
            .Returns(10.0m);

        var enforcer = CreateEnforcer();
        await enforcer.StartAsync(ct);

        _bus.Publish(CreateCostEvent("agent-a", 10.5m)); // 105% of tenant budget
        await Task.Delay(500, ct);

        await _eventBus.Received().PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.Severity == ActivitySeverity.Error &&
                e.Source.Scheme == "tenant"),
            Arg.Any<CancellationToken>());

        await enforcer.StopAsync(ct);
        enforcer.Dispose();
    }

    [Fact]
    public async Task CheckBudget_NoTenantBudget_NoTenantEventEmitted()
    {
        var ct = TestContext.Current.CancellationToken;
        _stateStore.GetAsync<decimal?>($"default:{StateKeys.TenantCostBudget}", Arg.Any<CancellationToken>())
            .Returns((decimal?)null);

        var enforcer = CreateEnforcer();
        await enforcer.StartAsync(ct);

        _bus.Publish(CreateCostEvent("agent-a", 100.0m));
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
    }
}