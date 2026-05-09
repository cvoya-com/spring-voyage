// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Units;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Hosted service that reconciles the persistent
/// <c>unit_subunit_memberships</c> projection with the authoritative
/// <c>unit://</c>-scheme entries kept in <c>UnitActor</c> state on host
/// startup (#1154). Lets the projection survive process restarts on
/// existing deployments where the column did not exist before this
/// migration, and recovers from any drift caused by an actor
/// write-through that failed mid-call.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Single-owner invariant.</strong> Mirrors
/// <see cref="Cvoya.Spring.Dapr.Data.DatabaseMigrator"/> /
/// <see cref="Tenancy.DefaultTenantBootstrapService"/>: registered
/// exactly once per deployment from the host that owns the actor
/// runtime (the Worker in OSS topology). Multiple replicas of the
/// same host will run the reconciliation concurrently — every write
/// goes through <see cref="IUnitSubunitMembershipProjector"/> so the
/// upserts are idempotent and races collapse on PK; running it more
/// than once does not corrupt the projection but does pay extra
/// per-actor round-trips.
/// </para>
/// <para>
/// <strong>Best-effort.</strong> A directory service that has no
/// units, an actor that fails to respond, or a database that rejects
/// the upsert all log + continue. The startup path must never block
/// on a transient projection failure — the actor-state list remains
/// the runtime source of truth and the worker can serve traffic with
/// a stale tree until the next reconciliation pass.
/// </para>
/// <para>
/// <strong>Runs after <c>ApplicationStarted</c>.</strong> The
/// reconciliation issues outbound <see cref="IUnitActor"/> proxy
/// calls. Those calls travel through the Dapr sidecar, which has to
/// call back into the worker on its app port to activate / route to
/// the actor — so the worker must be listening before we issue them.
/// <see cref="IHostedService.StartAsync"/> runs <em>before</em>
/// Kestrel binds, which is why this service derives from
/// <see cref="BackgroundService"/> and gates the reconciliation on
/// <see cref="IHostApplicationLifetime.ApplicationStarted"/>: a
/// previous incarnation that ran the reconciliation directly inside
/// <c>StartAsync</c> deadlocked on the sidecar callback for 100&#8239;s,
/// timed out with <see cref="TaskCanceledException"/> (which the old
/// "ex is not <see cref="OperationCanceledException"/>" filter let
/// escape), and crashed the worker into a restart loop that prevented
/// every actor — <see cref="AgentActor"/> included — from ever
/// registering with Dapr placement. See the
/// <c>did not find address for actor 'AgentActor/&lt;guid&gt;'</c>
/// regression after the #1155 deployment.
/// </para>
/// </remarks>
public class UnitSubunitMembershipReconciliationService(
    IServiceScopeFactory scopeFactory,
    IDirectoryService directoryService,
    IActorProxyFactory actorProxyFactory,
    IUnitSubunitMembershipProjector projector,
    ITenantScopeBypass tenantScopeBypass,
    IHostApplicationLifetime applicationLifetime,
    ILogger<UnitSubunitMembershipReconciliationService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Defer until the host has finished startup so Kestrel is
        // listening on the app port. The Dapr sidecar's actor invoke
        // path makes a callback into this same worker process; if we
        // call out before the listener binds, the sidecar burns its
        // 100-second app-channel timeout waiting and the resulting
        // TaskCanceledException would otherwise propagate out of a
        // hosted-service StartAsync and crash the worker. See the
        // remarks block for the historical regression this guards.
        if (!await WaitForApplicationStartedAsync(stoppingToken))
        {
            return;
        }

        try
        {
            await ReconcileAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            // Reconciliation is best-effort. We swallow every failure
            // that is not a host-driven shutdown — including HTTP
            // timeouts (TaskCanceledException, which derives from
            // OperationCanceledException and would have escaped the
            // pre-#1155 fix-up filter). Serving traffic with a stale
            // tenant tree until the next boot is strictly better than
            // taking the worker — and with it the entire actor
            // runtime — down on a transient projection error.
            logger.LogError(ex,
                "Sub-unit membership reconciliation failed; continuing with whatever projection rows already exist.");
        }
    }

    private async Task<bool> WaitForApplicationStartedAsync(CancellationToken stoppingToken)
    {
        if (applicationLifetime.ApplicationStarted.IsCancellationRequested)
        {
            return true;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var startedRegistration = applicationLifetime.ApplicationStarted.Register(
            static state => ((TaskCompletionSource)state!).TrySetResult(), tcs);
        using var stopRegistration = stoppingToken.Register(
            static state => ((TaskCompletionSource)state!).TrySetCanceled(), tcs);

        try
        {
            await tcs.Task;
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        // Tenancy bypass: the reconciliation runs before any per-request
        // tenant context exists, but the upserts hit a tenant-scoped
        // table whose query filter would otherwise see an empty database.
        // Mirrors the bypass scope used by the database migrator and
        // default-tenant bootstrap. The projector resolves a fresh DI
        // scope per write; the bypass flows through async-locals so the
        // EF context inside that scope sees the bypass.
        using var bypass = tenantScopeBypass.BeginBypass("sub-unit membership reconciliation");

        var entries = await directoryService.ListAllAsync(cancellationToken);
        var unitEntries = entries
            .Where(e => string.Equals(e.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (unitEntries.Count == 0)
        {
            logger.LogDebug(
                "Sub-unit membership reconciliation: directory has no units; nothing to project.");
            return;
        }

        // Snapshot the existing projection so we can both fill in
        // missing edges and retire ghost edges that point at units the
        // actor no longer carries. Reading happens through the same
        // scoped repository that handles writes — the bypass above
        // covers the read filter.
        var existingEdges = await ReadExistingEdgesAsync(cancellationToken);

        var liveEdges = new HashSet<(Guid Parent, Guid Child)>();
        var visited = 0;
        var added = 0;
        var failed = 0;

        foreach (var unit in unitEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                    new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(unit.ActorId)), nameof(UnitActor));
                var members = await proxy.GetMembersAsync(cancellationToken);

                foreach (var member in members)
                {
                    if (!string.Equals(member.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var edgeKey = (unit.Address.Id, member.Id);
                    liveEdges.Add(edgeKey);

                    if (existingEdges.Contains(edgeKey))
                    {
                        // Already projected — skip the upsert to avoid
                        // a needless UpdatedAt bump.
                        continue;
                    }

                    await projector.ProjectAddAsync(unit.Address.Id, member.Id, cancellationToken);
                    added++;
                }

                visited++;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Any per-unit failure (including a sidecar HTTP timeout —
                // which surfaces as TaskCanceledException, an
                // OperationCanceledException — when a single actor is slow
                // to activate) just skips that unit; we still want to
                // project edges for the rest of the directory.
                logger.LogWarning(ex,
                    "Sub-unit membership reconciliation: failed to read members from unit {UnitPath}; skipping.",
                    unit.Address.Path);
                failed++;
            }
        }

        // Retire any projection row whose edge is no longer present in
        // actor state. Skip rows that mention units we never visited
        // (failed read) — without a successful actor read we cannot
        // tell stale edges from edges we just couldn't observe.
        var visitedParents = unitEntries
            .Select(u => u.Address.Id)
            .ToHashSet();

        var removed = 0;
        foreach (var edge in existingEdges)
        {
            if (!visitedParents.Contains(edge.Parent))
            {
                continue;
            }

            if (liveEdges.Contains(edge))
            {
                continue;
            }

            try
            {
                await projector.ProjectRemoveAsync(edge.Parent, edge.Child, cancellationToken);
                removed++;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex,
                    "Sub-unit membership reconciliation: failed to retire ghost edge {Parent} → {Child}.",
                    edge.Parent, edge.Child);
            }
        }

        logger.LogInformation(
            "Sub-unit membership reconciliation complete: visited={Visited}, failed={Failed}, added={Added}, retired={Removed}, live={Live}.",
            visited, failed, added, removed, liveEdges.Count);
    }

    private async Task<HashSet<(Guid Parent, Guid Child)>> ReadExistingEdgesAsync(
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUnitSubunitMembershipRepository>();
        var rows = await repo.ListAllAsync(cancellationToken);
        return rows
            .Select(r => (r.ParentUnitId, r.ChildUnitId))
            .ToHashSet();
    }
}
