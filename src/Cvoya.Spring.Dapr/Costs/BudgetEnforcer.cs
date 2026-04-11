// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Costs;

using System.Collections.Concurrent;
using System.Reactive.Linq;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Observability;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Hosted service that monitors cost events and enforces per-agent and tenant-level budgets.
/// Emits warning events at 80% of budget and error events at 100%.
/// When an agent exceeds its budget, the enforcer pauses the agent's initiative
/// by writing a "Paused" initiative state to the state store.
/// </summary>
public sealed partial class BudgetEnforcer(
    ActivityEventBus bus,
    IActivityEventBus eventBus,
    IStateStore stateStore,
    ILogger<BudgetEnforcer> logger) : IHostedService, IDisposable
{
    private IDisposable? _subscription;
    private readonly ConcurrentDictionary<string, decimal> _accumulatedCosts = new();
    private readonly ConcurrentDictionary<string, bool> _warningEmitted = new();
    private readonly ConcurrentDictionary<string, bool> _errorEmitted = new();
    private decimal _tenantAccumulatedCost;
    private bool _tenantWarningEmitted;
    private bool _tenantErrorEmitted;
    private readonly object _tenantLock = new();

    internal const decimal WarningThreshold = 0.8m;
    internal const decimal ErrorThreshold = 1.0m;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = bus.Events
            .Where(e => e.EventType == ActivityEventType.CostIncurred)
            .Subscribe(
                e => Task.Run(() => CheckBudgetAsync(e)).GetAwaiter().GetResult(),
                ex => LogStreamFaulted(logger, ex));

        LogStarted(logger);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        LogStopped(logger);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _subscription?.Dispose();
    }

    private async Task CheckBudgetAsync(ActivityEvent costEvent)
    {
        try
        {
            var agentId = costEvent.Source.Path;
            var cost = costEvent.Cost ?? 0m;

            if (cost <= 0m)
            {
                return;
            }

            await CheckAgentBudgetAsync(agentId, cost, costEvent.CorrelationId);
            await CheckTenantBudgetAsync(cost, costEvent.CorrelationId);
        }
        catch (Exception ex)
        {
            LogCheckFailed(logger, costEvent.Source.Path, ex);
        }
    }

    private async Task CheckAgentBudgetAsync(string agentId, decimal cost, string? correlationId)
    {
        var accumulated = _accumulatedCosts.AddOrUpdate(agentId, cost, (_, existing) => existing + cost);

        var budgetKey = $"{agentId}:{StateKeys.AgentCostBudget}";
        var budget = await stateStore.GetAsync<decimal?>(budgetKey);

        if (budget is null or <= 0m)
        {
            return;
        }

        var ratio = accumulated / budget.Value;

        if (ratio >= ErrorThreshold && !_errorEmitted.ContainsKey(agentId))
        {
            _errorEmitted[agentId] = true;
            await EmitBudgetEventAsync(agentId, ActivitySeverity.Error, accumulated, budget.Value, correlationId);
            await PauseAgentInitiativeAsync(agentId);
            LogBudgetExceeded(logger, agentId, accumulated, budget.Value);
        }
        else if (ratio >= WarningThreshold && !_warningEmitted.ContainsKey(agentId))
        {
            _warningEmitted[agentId] = true;
            await EmitBudgetEventAsync(agentId, ActivitySeverity.Warning, accumulated, budget.Value, correlationId);
            LogBudgetWarning(logger, agentId, accumulated, budget.Value);
        }
    }

    private async Task CheckTenantBudgetAsync(decimal cost, string? correlationId)
    {
        decimal accumulated;
        lock (_tenantLock)
        {
            _tenantAccumulatedCost += cost;
            accumulated = _tenantAccumulatedCost;
        }

        var tenantId = "default";
        var budgetKey = $"{tenantId}:{StateKeys.TenantCostBudget}";
        var budget = await stateStore.GetAsync<decimal?>(budgetKey);

        if (budget is null or <= 0m)
        {
            return;
        }

        var ratio = accumulated / budget.Value;

        if (ratio >= ErrorThreshold && !_tenantErrorEmitted)
        {
            _tenantErrorEmitted = true;
            await EmitTenantBudgetEventAsync(tenantId, ActivitySeverity.Error, accumulated, budget.Value, correlationId);
            LogTenantBudgetExceeded(logger, tenantId, accumulated, budget.Value);
        }
        else if (ratio >= WarningThreshold && !_tenantWarningEmitted)
        {
            _tenantWarningEmitted = true;
            await EmitTenantBudgetEventAsync(tenantId, ActivitySeverity.Warning, accumulated, budget.Value, correlationId);
            LogTenantBudgetWarning(logger, tenantId, accumulated, budget.Value);
        }
    }

    /// <summary>
    /// Pauses the agent's initiative by writing a paused state to the state store.
    /// </summary>
    private async Task PauseAgentInitiativeAsync(string agentId)
    {
        try
        {
            var initiativeKey = $"{agentId}:{StateKeys.InitiativeState}";
            await stateStore.SetAsync(initiativeKey, new InitiativePausedState("BudgetExceeded", DateTimeOffset.UtcNow));
            LogInitiativePaused(logger, agentId);
        }
        catch (Exception ex)
        {
            LogInitiativePauseFailed(logger, agentId, ex);
        }
    }

    private async Task EmitBudgetEventAsync(
        string agentId,
        ActivitySeverity severity,
        decimal accumulated,
        decimal budget,
        string? correlationId)
    {
        var summary = severity == ActivitySeverity.Error
            ? $"Agent '{agentId}' has exceeded its cost budget ({accumulated:C} / {budget:C})"
            : $"Agent '{agentId}' is approaching its cost budget ({accumulated:C} / {budget:C})";

        var budgetEvent = new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new Address("agent", agentId),
            ActivityEventType.CostIncurred,
            severity,
            summary,
            CorrelationId: correlationId);

        await eventBus.PublishAsync(budgetEvent);
    }

    private async Task EmitTenantBudgetEventAsync(
        string tenantId,
        ActivitySeverity severity,
        decimal accumulated,
        decimal budget,
        string? correlationId)
    {
        var summary = severity == ActivitySeverity.Error
            ? $"Tenant '{tenantId}' has exceeded its cost budget ({accumulated:C} / {budget:C})"
            : $"Tenant '{tenantId}' is approaching its cost budget ({accumulated:C} / {budget:C})";

        var budgetEvent = new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new Address("tenant", tenantId),
            ActivityEventType.CostIncurred,
            severity,
            summary,
            CorrelationId: correlationId);

        await eventBus.PublishAsync(budgetEvent);
    }

    [LoggerMessage(EventId = 2310, Level = LogLevel.Information, Message = "BudgetEnforcer started")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(EventId = 2311, Level = LogLevel.Information, Message = "BudgetEnforcer stopped")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(EventId = 2312, Level = LogLevel.Warning, Message = "Agent '{AgentId}' approaching cost budget: {Accumulated:C} of {Budget:C}")]
    private static partial void LogBudgetWarning(ILogger logger, string agentId, decimal accumulated, decimal budget);

    [LoggerMessage(EventId = 2313, Level = LogLevel.Error, Message = "Agent '{AgentId}' exceeded cost budget: {Accumulated:C} of {Budget:C}")]
    private static partial void LogBudgetExceeded(ILogger logger, string agentId, decimal accumulated, decimal budget);

    [LoggerMessage(EventId = 2314, Level = LogLevel.Error, Message = "Budget check failed for agent '{AgentId}'")]
    private static partial void LogCheckFailed(ILogger logger, string agentId, Exception exception);

    [LoggerMessage(EventId = 2315, Level = LogLevel.Error, Message = "BudgetEnforcer stream faulted")]
    private static partial void LogStreamFaulted(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2316, Level = LogLevel.Information, Message = "Agent '{AgentId}' initiative paused due to budget exceeded")]
    private static partial void LogInitiativePaused(ILogger logger, string agentId);

    [LoggerMessage(EventId = 2317, Level = LogLevel.Error, Message = "Failed to pause initiative for agent '{AgentId}'")]
    private static partial void LogInitiativePauseFailed(ILogger logger, string agentId, Exception exception);

    [LoggerMessage(EventId = 2318, Level = LogLevel.Warning, Message = "Tenant '{TenantId}' approaching cost budget: {Accumulated:C} of {Budget:C}")]
    private static partial void LogTenantBudgetWarning(ILogger logger, string tenantId, decimal accumulated, decimal budget);

    [LoggerMessage(EventId = 2319, Level = LogLevel.Error, Message = "Tenant '{TenantId}' exceeded cost budget: {Accumulated:C} of {Budget:C}")]
    private static partial void LogTenantBudgetExceeded(ILogger logger, string tenantId, decimal accumulated, decimal budget);
}