// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Costs;

using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Observability;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Hosted service that monitors cost events and enforces per-agent, per-unit,
/// and tenant-level <b>daily</b> budgets. Emits a warning at 80% of the daily
/// budget and an error at 100%; when an agent (or unit) crosses 100% the
/// enforcer pauses its initiative by writing a "Paused" initiative state.
///
/// <para>
/// Budget limits are read from the tenant-scoped <c>budget_limits</c> EF table
/// per ADR-0040. <b>Spend</b> is read from the authoritative <c>cost_records</c>
/// ledger over a rolling-from-UTC-midnight window (#3073) — not an in-memory
/// lifetime accumulator. That fixes two bugs in the pre-#3073 enforcer: a
/// "daily" budget that never reset (it summed spend for the whole process
/// lifetime), and spend state that was lost entirely on a host restart. The
/// once-per-day warning / error guards are keyed by <c>(scope, UTC-day)</c> so
/// they fire once per scope per day and reset at the day boundary.
/// </para>
/// <para>
/// Initiative pausing remains a state-store write because the
/// <c>Agent:InitiativeState</c> key is runtime-ephemeral scratch (ADR-0040
/// matrix row "Stay").
/// </para>
/// </summary>
public sealed partial class BudgetEnforcer(
    ActivityEventBus bus,
    IActivityEventBus eventBus,
    IServiceScopeFactory scopeFactory,
    IStateStore stateStore,
    ILogger<BudgetEnforcer> logger) : IHostedService, IDisposable
{
    private IDisposable? _subscription;

    // Once-per-(scope, UTC-day) guards. TryAdd returns true exactly once per
    // key, so a warning / error fires at most once per scope per day and a new
    // day's key resets the guard. Keyed by (id, day) for agent / unit; by day
    // alone for the single tenant scope.
    private readonly ConcurrentDictionary<(Guid Id, DateOnly Day), byte> _agentWarned = new();
    private readonly ConcurrentDictionary<(Guid Id, DateOnly Day), byte> _agentErrored = new();
    private readonly ConcurrentDictionary<(Guid Id, DateOnly Day), byte> _unitWarned = new();
    private readonly ConcurrentDictionary<(Guid Id, DateOnly Day), byte> _unitErrored = new();
    private readonly ConcurrentDictionary<DateOnly, byte> _tenantWarned = new();
    private readonly ConcurrentDictionary<DateOnly, byte> _tenantErrored = new();

    internal const decimal WarningThreshold = 0.8m;
    internal const decimal ErrorThreshold = 1.0m;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = bus.Events
            .Where(e => e.EventType == ActivityEventType.CostIncurred)
            .Subscribe(
                e => { _ = Task.Run(() => CheckBudgetAsync(e)); },
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
            var cost = costEvent.Cost ?? 0m;

            // The enforcer's own budget warning / error events are CostIncurred
            // with no cost — skip them (and any zero-cost turn) so we neither
            // recurse nor query for a non-event.
            if (cost <= 0m)
            {
                return;
            }

            var agentId = costEvent.Source.Id;
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            var dayStart = new DateTimeOffset(DateTime.UtcNow.Date, TimeSpan.Zero);

            await CheckAgentBudgetAsync(agentId, cost, today, dayStart, costEvent.CorrelationId);

            var unitId = TryReadUnitId(costEvent);
            if (unitId is { } unit)
            {
                await CheckUnitBudgetAsync(unit, cost, today, dayStart, costEvent.CorrelationId);
            }

            await CheckTenantBudgetAsync(cost, today, dayStart, costEvent.CorrelationId);
        }
        catch (Exception ex)
        {
            LogCheckFailed(logger, costEvent.Source.Path, ex);
        }
    }

    private async Task CheckAgentBudgetAsync(
        Guid agentId, decimal cost, DateOnly today, DateTimeOffset dayStart, string? correlationId)
    {
        var budget = await GetBudgetAsync(BudgetLimitScope.Agent, agentId);
        if (budget is null or <= 0m)
        {
            return;
        }

        var spentToday = await GetSpentTodayAsync(BudgetLimitScope.Agent, agentId, dayStart) + cost;
        var ratio = spentToday / budget.Value;
        var agentPath = GuidFormatter.Format(agentId);
        var key = (agentId, today);

        if (ratio >= ErrorThreshold && _agentErrored.TryAdd(key, 0))
        {
            await EmitBudgetEventAsync(
                new Address(Address.AgentScheme, agentId), "Agent", agentPath,
                ActivitySeverity.Error, spentToday, budget.Value, correlationId);
            await PauseInitiativeAsync(agentPath);
            LogBudgetExceeded(logger, agentPath, spentToday, budget.Value);
        }
        else if (ratio >= WarningThreshold && _agentWarned.TryAdd(key, 0))
        {
            await EmitBudgetEventAsync(
                new Address(Address.AgentScheme, agentId), "Agent", agentPath,
                ActivitySeverity.Warning, spentToday, budget.Value, correlationId);
            LogBudgetWarning(logger, agentPath, spentToday, budget.Value);
        }
    }

    private async Task CheckUnitBudgetAsync(
        Guid unitId, decimal cost, DateOnly today, DateTimeOffset dayStart, string? correlationId)
    {
        var budget = await GetBudgetAsync(BudgetLimitScope.Unit, unitId);
        if (budget is null or <= 0m)
        {
            return;
        }

        var spentToday = await GetSpentTodayAsync(BudgetLimitScope.Unit, unitId, dayStart) + cost;
        var ratio = spentToday / budget.Value;
        var unitPath = GuidFormatter.Format(unitId);
        var key = (unitId, today);

        if (ratio >= ErrorThreshold && _unitErrored.TryAdd(key, 0))
        {
            await EmitBudgetEventAsync(
                new Address(Address.UnitScheme, unitId), "Unit", unitPath,
                ActivitySeverity.Error, spentToday, budget.Value, correlationId);
            await PauseInitiativeAsync(unitPath);
            LogBudgetExceeded(logger, unitPath, spentToday, budget.Value);
        }
        else if (ratio >= WarningThreshold && _unitWarned.TryAdd(key, 0))
        {
            await EmitBudgetEventAsync(
                new Address(Address.UnitScheme, unitId), "Unit", unitPath,
                ActivitySeverity.Warning, spentToday, budget.Value, correlationId);
            LogBudgetWarning(logger, unitPath, spentToday, budget.Value);
        }
    }

    private async Task CheckTenantBudgetAsync(
        decimal cost, DateOnly today, DateTimeOffset dayStart, string? correlationId)
    {
        var budget = await GetTenantBudgetAsync();
        if (budget is null or <= 0m)
        {
            return;
        }

        // OSS hosts run with the single canonical tenant id; the cloud overlay
        // swaps this enforcer for a tenant-aware version that partitions per
        // tenant.
        var tenantId = OssTenantIds.Default;
        var tenantWireId = OssTenantIds.DefaultNoDash;

        var spentToday = await GetTenantSpentTodayAsync(tenantId, dayStart) + cost;
        var ratio = spentToday / budget.Value;

        if (ratio >= ErrorThreshold && _tenantErrored.TryAdd(today, 0))
        {
            await EmitTenantBudgetEventAsync(tenantWireId, ActivitySeverity.Error, spentToday, budget.Value, correlationId);
            LogTenantBudgetExceeded(logger, tenantWireId, spentToday, budget.Value);
        }
        else if (ratio >= WarningThreshold && _tenantWarned.TryAdd(today, 0))
        {
            await EmitTenantBudgetEventAsync(tenantWireId, ActivitySeverity.Warning, spentToday, budget.Value, correlationId);
            LogTenantBudgetWarning(logger, tenantWireId, spentToday, budget.Value);
        }
    }

    /// <summary>Reads the configured daily budget for an agent / unit scope row.</summary>
    private async Task<decimal?> GetBudgetAsync(string scopeType, Guid scopeId)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var row = await dbContext.BudgetLimits
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.ScopeType == scopeType && b.ScopeId == scopeId);

        return row?.DailyBudget;
    }

    private async Task<decimal?> GetTenantBudgetAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var row = await dbContext.BudgetLimits
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.ScopeType == BudgetLimitScope.Tenant && b.ScopeId == null);

        return row?.DailyBudget;
    }

    /// <summary>
    /// Sums spend for an agent / unit scope from the <c>cost_records</c> ledger
    /// since <paramref name="dayStart"/> (UTC midnight). The caller adds the
    /// in-flight turn's cost on top, because the <c>CostTracker</c> persists on
    /// a 1-second batch and has almost certainly not written the triggering
    /// record yet — counting it immediately makes the enforcer trip on the
    /// crossing turn rather than one turn late.
    /// </summary>
    private async Task<decimal> GetSpentTodayAsync(string scopeType, Guid scopeId, DateTimeOffset dayStart)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var query = dbContext.CostRecords.AsNoTracking().Where(r => r.Timestamp >= dayStart);
        query = scopeType == BudgetLimitScope.Unit
            ? query.Where(r => r.UnitId == scopeId)
            : query.Where(r => r.AgentId == scopeId);

        // SumAsync over an empty set returns 0.
        return await query.SumAsync(r => r.Cost);
    }

    private async Task<decimal> GetTenantSpentTodayAsync(Guid tenantId, DateTimeOffset dayStart)
    {
        using var scope = scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        return await dbContext.CostRecords
            .AsNoTracking()
            .Where(r => r.TenantId == tenantId && r.Timestamp >= dayStart)
            .SumAsync(r => r.Cost);
    }

    /// <summary>
    /// Reads the owning-unit id off the cost event's <c>details.unitId</c>
    /// (the canonical Guid wire form the dispatch coordinator stamps). Returns
    /// <c>null</c> when the turn had no owning unit or the value is malformed.
    /// </summary>
    private static Guid? TryReadUnitId(ActivityEvent costEvent)
    {
        if (costEvent.Details is not { } details
            || details.ValueKind != JsonValueKind.Object
            || !details.TryGetProperty("unitId", out var unitProp)
            || unitProp.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return GuidFormatter.TryParse(unitProp.GetString(), out var unitId) ? unitId : null;
    }

    /// <summary>
    /// Pauses an agent's / unit's initiative by writing a paused state to the
    /// state store.
    /// </summary>
    private async Task PauseInitiativeAsync(string scopePath)
    {
        try
        {
            var initiativeKey = $"{scopePath}:{StateKeys.InitiativeState}";
            await stateStore.SetAsync(initiativeKey, new InitiativePausedState("BudgetExceeded", DateTimeOffset.UtcNow));
            LogInitiativePaused(logger, scopePath);
        }
        catch (Exception ex)
        {
            LogInitiativePauseFailed(logger, scopePath, ex);
        }
    }

    private async Task EmitBudgetEventAsync(
        Address source,
        string scopeKind,
        string scopePath,
        ActivitySeverity severity,
        decimal spentToday,
        decimal budget,
        string? correlationId)
    {
        var summary = severity == ActivitySeverity.Error
            ? $"{scopeKind} '{scopePath}' has exceeded its daily cost budget ({spentToday:C} / {budget:C})"
            : $"{scopeKind} '{scopePath}' is approaching its daily cost budget ({spentToday:C} / {budget:C})";

        var budgetEvent = new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            source,
            ActivityEventType.CostIncurred,
            severity,
            summary,
            CorrelationId: correlationId);

        await eventBus.PublishAsync(budgetEvent);
    }

    private async Task EmitTenantBudgetEventAsync(
        string tenantWireId,
        ActivitySeverity severity,
        decimal spentToday,
        decimal budget,
        string? correlationId)
    {
        var summary = severity == ActivitySeverity.Error
            ? $"Tenant '{tenantWireId}' has exceeded its daily cost budget ({spentToday:C} / {budget:C})"
            : $"Tenant '{tenantWireId}' is approaching its daily cost budget ({spentToday:C} / {budget:C})";

        var budgetEvent = new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            Address.For("tenant", tenantWireId),
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

    [LoggerMessage(EventId = 2312, Level = LogLevel.Warning, Message = "Scope '{ScopeId}' approaching daily cost budget: {Spent:C} of {Budget:C}")]
    private static partial void LogBudgetWarning(ILogger logger, string scopeId, decimal spent, decimal budget);

    [LoggerMessage(EventId = 2313, Level = LogLevel.Error, Message = "Scope '{ScopeId}' exceeded daily cost budget: {Spent:C} of {Budget:C}")]
    private static partial void LogBudgetExceeded(ILogger logger, string scopeId, decimal spent, decimal budget);

    [LoggerMessage(EventId = 2314, Level = LogLevel.Error, Message = "Budget check failed for '{ScopeId}'")]
    private static partial void LogCheckFailed(ILogger logger, string scopeId, Exception exception);

    [LoggerMessage(EventId = 2315, Level = LogLevel.Error, Message = "BudgetEnforcer stream faulted")]
    private static partial void LogStreamFaulted(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2316, Level = LogLevel.Information, Message = "Initiative for '{ScopeId}' paused due to budget exceeded")]
    private static partial void LogInitiativePaused(ILogger logger, string scopeId);

    [LoggerMessage(EventId = 2317, Level = LogLevel.Error, Message = "Failed to pause initiative for '{ScopeId}'")]
    private static partial void LogInitiativePauseFailed(ILogger logger, string scopeId, Exception exception);

    [LoggerMessage(EventId = 2318, Level = LogLevel.Warning, Message = "Tenant '{TenantId}' approaching daily cost budget: {Spent:C} of {Budget:C}")]
    private static partial void LogTenantBudgetWarning(ILogger logger, string tenantId, decimal spent, decimal budget);

    [LoggerMessage(EventId = 2319, Level = LogLevel.Error, Message = "Tenant '{TenantId}' exceeded daily cost budget: {Spent:C} of {Budget:C}")]
    private static partial void LogTenantBudgetExceeded(ILogger logger, string tenantId, decimal spent, decimal budget);
}
