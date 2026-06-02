// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Host.Api.Models;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.Logging;

/// <summary>
/// Maps dashboard API endpoints for agent, unit, and cost summaries.
/// </summary>
public static class DashboardEndpoints
{
    /// <summary>
    /// Per-actor wall-clock budget for the parallel status fan-out (#2584).
    /// A busy actor (one mid-turn, holding its non-reentrant turn lock) cannot
    /// answer <c>GetStatusAsync</c> within the budget; #2981 reports its
    /// real last-known status from the queryable lifecycle mirror instead of
    /// fabricating <see cref="LifecycleStatus.Starting"/> — the fabrication
    /// that made a stopped-but-busy unit look like it was starting.
    /// </summary>
    private static readonly TimeSpan StatusReadBudget = TimeSpan.FromSeconds(1.5);

    /// <summary>
    /// Registers dashboard endpoints on the specified endpoint route builder.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The route group builder for chaining.</returns>
    public static RouteGroupBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/tenant/dashboard")
            .WithTags("Dashboard");

        group.MapGet("/summary", GetDashboardSummaryAsync)
            .WithName("GetDashboardSummary")
            .WithSummary("Get an aggregated dashboard summary with unit/agent counts, status breakdown, recent activity, and total cost")
            .Produces<DashboardSummary>(StatusCodes.Status200OK);

        group.MapGet("/agents", GetAgentsSummaryAsync)
            .WithName("GetAgentsSummary")
            .WithSummary("Get a summary of all registered agents")
            .Produces<AgentDashboardSummary[]>(StatusCodes.Status200OK);

        group.MapGet("/units", GetUnitsSummaryAsync)
            .WithName("GetUnitsSummary")
            .WithSummary("Get a summary of all registered units")
            .Produces<UnitDashboardSummary[]>(StatusCodes.Status200OK);

        group.MapGet("/costs", GetCostsSummaryAsync)
            .WithName("GetCostsSummary")
            .WithSummary("Get aggregated cost data")
            .Produces<CostDashboardSummary>(StatusCodes.Status200OK);

        return group;
    }

    private static async Task<IResult> GetDashboardSummaryAsync(
        IDirectoryService directoryService,
        IActorProxyFactory actorProxyFactory,
        IActivityQueryService activityQueryService,
        ILifecycleStatusStore lifecycleStatusStore,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.DashboardEndpoints");
        var entries = await directoryService.ListAllAsync(cancellationToken);

        // Count agents.
        var agentCount = entries
            .Count(e => string.Equals(e.Address.Scheme, "agent", StringComparison.OrdinalIgnoreCase));

        // Count units and gather status breakdown.
        var unitEntries = entries
            .Where(e => string.Equals(e.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // #2584: parallel + budget-bounded fan-out for the per-unit status
        // reads. A unit actor that is busy starting its container instance
        // would previously stall the entire dashboard; now it is reported
        // as Starting (with a per-card indicator on the portal) while the
        // page renders.
        var unitStatusPairs = await Task.WhenAll(unitEntries.Select(async e =>
            (Entry: e,
             Status: await TryReadUnitLifecycleStatusAsync(actorProxyFactory, lifecycleStatusStore, e.ActorId, e.Address.Path, logger, cancellationToken))));

        var statusCounts = new Dictionary<LifecycleStatus, int>();
        var unitSummaries = new List<UnitDashboardSummary>(unitEntries.Count);
        foreach (var (e, status) in unitStatusPairs)
        {
            statusCounts[status] = statusCounts.TryGetValue(status, out var count) ? count + 1 : 1;
            unitSummaries.Add(new UnitDashboardSummary(e.Address.Path, e.DisplayName, e.RegisteredAt, status));
        }

        // Agent summaries.
        var agentSummaries = entries
            .Where(e => string.Equals(e.Address.Scheme, "agent", StringComparison.OrdinalIgnoreCase))
            .Select(e => new AgentDashboardSummary(e.Address.Path, e.DisplayName, e.Role, e.RegisteredAt))
            .ToList();

        // Recent activity (last 10).
        var activityResult = await activityQueryService.QueryAsync(
            new Core.Observability.ActivityQueryParameters(PageSize: 10),
            cancellationToken);

        // Total cost.
        var totalCost = await activityQueryService.GetTotalCostAsync(null, null, null, cancellationToken);

        var summary = new DashboardSummary(
            unitEntries.Count,
            statusCounts,
            agentCount,
            activityResult.Items,
            totalCost,
            unitSummaries,
            agentSummaries);

        return Results.Ok(summary);
    }

    private static async Task<IResult> GetAgentsSummaryAsync(
        IDirectoryService directoryService,
        CancellationToken cancellationToken)
    {
        var entries = await directoryService.ListAllAsync(cancellationToken);

        var agents = entries
            .Where(e => string.Equals(e.Address.Scheme, "agent", StringComparison.OrdinalIgnoreCase))
            .Select(e => new AgentDashboardSummary(e.Address.Path, e.DisplayName, e.Role, e.RegisteredAt))
            .ToList();

        return Results.Ok(agents);
    }

    private static async Task<IResult> GetUnitsSummaryAsync(
        IDirectoryService directoryService,
        IActorProxyFactory actorProxyFactory,
        ILifecycleStatusStore lifecycleStatusStore,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var logger = loggerFactory.CreateLogger("Cvoya.Spring.Host.Api.Endpoints.DashboardEndpoints");
        var entries = await directoryService.ListAllAsync(cancellationToken);

        var unitEntries = entries
            .Where(e => string.Equals(e.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // #2584: parallel + budget-bounded fan-out, same pattern as
        // GetDashboardSummaryAsync above.
        var unitStatusPairs = await Task.WhenAll(unitEntries.Select(async e =>
            (Entry: e,
             Status: await TryReadUnitLifecycleStatusAsync(actorProxyFactory, lifecycleStatusStore, e.ActorId, e.Address.Path, logger, cancellationToken))));

        var units = unitStatusPairs
            .Select(p => new UnitDashboardSummary(p.Entry.Address.Path, p.Entry.DisplayName, p.Entry.RegisteredAt, p.Status))
            .ToList();

        return Results.Ok(units);
    }

    /// <summary>
    /// Read a unit's status from its actor with a wall-clock budget so a
    /// busy actor never stalls the dashboard (#2584). #2981: on a budget
    /// timeout the unit's <em>real</em> last-known status is read from the
    /// queryable lifecycle mirror (<c>unit_live_config.lifecycle_status</c>)
    /// rather than fabricating <see cref="LifecycleStatus.Starting"/>; the
    /// mirror is written by the actor on every transition, so it reflects the
    /// truth without contending on the actor turn lock. Mirrors
    /// <see cref="TenantTreeEndpoints"/> — same fallback semantics.
    /// </summary>
    private static async Task<LifecycleStatus> TryReadUnitLifecycleStatusAsync(
        IActorProxyFactory actorProxyFactory,
        ILifecycleStatusStore lifecycleStatusStore,
        Guid actorId,
        string unitPath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        Task<LifecycleStatus> statusTask;
        try
        {
            var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorId)), nameof(UnitActor));
            statusTask = proxy.GetStatusAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller aborted — don't fabricate a status (#3006 finding I)
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to start status read for unit {UnitName}; falling back to lifecycle mirror.",
                unitPath);
            return await ReadMirroredStatusOrUnknownAsync(
                lifecycleStatusStore, actorId, unitPath, logger, cancellationToken);
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var timeoutTask = Task.Delay(StatusReadBudget, cts.Token);
            var completed = await Task.WhenAny(statusTask, timeoutTask).ConfigureAwait(false);
            if (completed == statusTask)
            {
                cts.Cancel();
                return await statusTask.ConfigureAwait(false);
            }

            logger.LogInformation(
                "Status read for unit {UnitName} exceeded {BudgetMs} ms; reading lifecycle mirror.",
                unitPath, StatusReadBudget.TotalMilliseconds);

            // Observe the still-running task so its late completion doesn't
            // surface as an UnobservedTaskException.
            _ = statusTask.ContinueWith(
                t =>
                {
                    if (t.IsFaulted)
                    {
                        logger.LogDebug(t.Exception,
                            "Late status read for unit {UnitName} faulted after timeout.",
                            unitPath);
                    }
                },
                TaskScheduler.Default);

            return await ReadMirroredStatusOrUnknownAsync(
                lifecycleStatusStore, actorId, unitPath, logger, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // caller aborted — don't fabricate a status (#3006 finding I)
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Failed to read status for unit {UnitName}; falling back to lifecycle mirror.",
                unitPath);
            return await ReadMirroredStatusOrUnknownAsync(
                lifecycleStatusStore, actorId, unitPath, logger, cancellationToken);
        }
    }

    /// <summary>
    /// #2981: reads a unit's mirrored lifecycle status, returning
    /// <see cref="LifecycleStatus.Unknown"/> when no mirror row exists or the
    /// read fails — an honest degraded indicator, never a fabricated state.
    /// </summary>
    private static async Task<LifecycleStatus> ReadMirroredStatusOrUnknownAsync(
        ILifecycleStatusStore lifecycleStatusStore,
        Guid actorId,
        string unitPath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            var mirrored = await lifecycleStatusStore.TryGetStatusAsync(
                ArtefactKind.Unit, actorId, cancellationToken);
            return mirrored ?? LifecycleStatus.Unknown;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Lifecycle mirror read failed for unit {UnitName}; reporting Unknown.",
                unitPath);
            return LifecycleStatus.Unknown;
        }
    }

    private static async Task<IResult> GetCostsSummaryAsync(
        IActivityQueryService queryService,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        var costsBySource = await queryService.GetCostBySourceAsync(from, to, cancellationToken);
        var totalCost = await queryService.GetTotalCostAsync(null, from, to, cancellationToken);

        var summary = new CostDashboardSummary(totalCost, costsBySource, from, to);
        return Results.Ok(summary);
    }
}
