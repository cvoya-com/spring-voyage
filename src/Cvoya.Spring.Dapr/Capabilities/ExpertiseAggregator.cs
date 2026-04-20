// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Capabilities;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IExpertiseAggregator"/> implementation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Walk.</b> Starting from the requested unit, the aggregator walks the
/// member graph breadth-first. Each frontier frame carries the address
/// currently being visited and the ordered <em>path</em> of addresses from
/// the aggregating unit down to that frame. For each frame the aggregator
/// reads (via <see cref="IExpertiseStore"/>) the contributor's own domains
/// and emits one <see cref="ExpertiseEntry"/> per domain.
/// </para>
/// <para>
/// <b>Recursion bound.</b> The walk is bounded by
/// <see cref="UnitActor.MaxCycleDetectionDepth"/>. Exceeding the bound
/// throws <see cref="ExpertiseAggregationException"/> — matching the
/// membership cycle-detection contract so operators see the same diagnostic
/// for both kinds of pathological graphs.
/// </para>
/// <para>
/// <b>Cycle guard.</b> Visited actor ids are tracked per-call in a
/// <see cref="HashSet{String}"/>. If the frontier re-enters a unit that has
/// already been visited, the frame is skipped — a benign DAG convergence
/// does not throw, but <em>reaching back to the aggregating unit itself</em>
/// does throw (the membership-time cycle check should have prevented it,
/// but if external state corruption slips one past, the aggregator refuses
/// to loop).
/// </para>
/// <para>
/// <b>Cache + propagation.</b> Computed snapshots are kept in a
/// <see cref="ConcurrentDictionary{TKey, TValue}"/> keyed by the aggregating
/// unit address. <see cref="InvalidateAsync(Address, CancellationToken)"/>
/// walks <em>up</em> — for an agent, through the agent's unit memberships;
/// for a unit, through directory-resolved parent pointers — evicting every
/// ancestor so the next read recomputes. The cache is intentionally naive
/// (no TTL, no LRU): aggregated expertise is small, writes are rare,
/// membership mutations invalidate precisely, and the private cloud repo
/// can layer a tenant-scoped cache over this implementation by wrapping
/// the interface in DI.
/// </para>
/// </remarks>
public class ExpertiseAggregator(
    IExpertiseStore expertiseStore,
    IDirectoryService directoryService,
    IActorProxyFactory actorProxyFactory,
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory) : IExpertiseAggregator
{
    /// <summary>
    /// Test-only overload that keeps the pre-existing ctor signature
    /// (taking <see cref="IUnitMembershipRepository"/> directly) alive so the
    /// unit tests under <c>Cvoya.Spring.Dapr.Tests</c> don't have to build a
    /// scoped service provider just to exercise the walk. Production code
    /// takes the <see cref="IServiceScopeFactory"/> path so the scoped
    /// repository gets a fresh scope per call.
    /// </summary>
    internal ExpertiseAggregator(
        IExpertiseStore expertiseStore,
        IDirectoryService directoryService,
        IActorProxyFactory actorProxyFactory,
        IUnitMembershipRepository membershipRepository,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
        : this(expertiseStore, directoryService, actorProxyFactory,
            new DirectRepositoryScopeFactory(membershipRepository),
            timeProvider, loggerFactory)
    {
    }

    /// <summary>
    /// Matches the membership cycle-detection bound on <see cref="UnitActor"/>
    /// so the aggregator and the member-add validation agree on what
    /// "maximum sensible nesting" means.
    /// </summary>
    internal const int MaxAggregationDepth = UnitActor.MaxCycleDetectionDepth;

    private readonly ILogger _logger = loggerFactory.CreateLogger<ExpertiseAggregator>();
    private readonly ConcurrentDictionary<string, AggregatedExpertise> _cache = new();

    /// <inheritdoc />
    public async Task<AggregatedExpertise> GetAsync(Address unit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unit);

        var cacheKey = ToKey(unit);
        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var snapshot = await ComputeAsync(unit, cancellationToken);

        // Last-write-wins — a concurrent recompute for the same unit is safe
        // because the output is deterministic given the current graph state.
        _cache[cacheKey] = snapshot;
        return snapshot;
    }

    /// <inheritdoc />
    public async Task InvalidateAsync(Address origin, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(origin);

        // Evict the origin itself if it's a unit, and every ancestor.
        if (string.Equals(origin.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
        {
            _cache.TryRemove(ToKey(origin), out _);
        }

        await foreach (var ancestor in EnumerateAncestorsAsync(origin, cancellationToken))
        {
            _cache.TryRemove(ToKey(ancestor), out _);
        }
    }

    /// <summary>
    /// Enumerates every unit that transitively contains <paramref name="origin"/>
    /// (or, for agent origins, every unit that has the agent as a member —
    /// plus those units' ancestors). Walks up through the membership
    /// repository for agents and through unit-unit <c>ParentUnit</c> links
    /// for unit-scheme origins. Bounded by
    /// <see cref="MaxAggregationDepth"/>; on cycle or over-depth the
    /// enumeration stops rather than throwing — invalidation must not fail
    /// the caller, even on a misconfigured graph.
    /// </summary>
    private async IAsyncEnumerable<Address> EnumerateAncestorsAsync(
        Address origin,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var queue = new Queue<(Address Address, int Depth)>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        if (string.Equals(origin.Scheme, "agent", StringComparison.OrdinalIgnoreCase))
        {
            // Seed the walk with every unit the agent belongs to. The
            // membership repository is scoped (per-request EF context), so
            // we resolve it through IServiceScopeFactory to keep the
            // aggregator registration lifetime-clean as a singleton.
            var memberships = await ListMembershipsAsync(origin.Path, cancellationToken);
            foreach (var m in memberships)
            {
                queue.Enqueue((new Address("unit", m.UnitId), 1));
            }
        }
        else if (string.Equals(origin.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
        {
            queue.Enqueue((origin, 0));
        }
        else
        {
            yield break;
        }

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();

            if (depth > MaxAggregationDepth)
            {
                _logger.LogWarning(
                    "Invalidate walk exceeded max depth {MaxDepth} starting from {Scheme}://{Path}; bailing out.",
                    MaxAggregationDepth, origin.Scheme, origin.Path);
                yield break;
            }

            if (!visited.Add(ToKey(current)))
            {
                continue;
            }

            // The current node is an ancestor (or the origin unit itself).
            if (depth > 0)
            {
                yield return current;
            }

            // Walk up through unit-unit containment. The membership
            // repository stores agent-unit edges only (per #160); unit-unit
            // parent pointers live on the containing unit's members list,
            // so we scan the directory for units that contain the current
            // node. Today's directories are small and the invalidation
            // path is rare; when the directory grows a reverse index this
            // method can be swapped out without touching the aggregator.
            foreach (var parent in await ListParentUnitsAsync(current, cancellationToken))
            {
                queue.Enqueue((parent, depth + 1));
            }
        }
    }

    /// <summary>
    /// Resolves every unit whose members list contains
    /// <paramref name="child"/>. Today's directory does not index the
    /// reverse membership so we scan <see cref="IDirectoryService.ListAllAsync"/>
    /// — fine for the current data volume and rare invalidations. When
    /// the directory grows a reverse index (follow-up), this method can
    /// be replaced without touching the aggregator.
    /// </summary>
    private async Task<IReadOnlyList<Address>> ListParentUnitsAsync(Address child, CancellationToken ct)
    {
        if (!string.Equals(child.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
        {
            return Array.Empty<Address>();
        }

        IReadOnlyList<DirectoryEntry> all;
        try
        {
            all = await directoryService.ListAllAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Invalidate: directory ListAll failed; stopping parent walk for {Child}.", child);
            return Array.Empty<Address>();
        }

        // #745: resolve the tenant guard from a fresh scope for the
        // invalidate parent walk. Optional so the test-only
        // DirectRepositoryScopeFactory (which only provides an
        // IUnitMembershipRepository) keeps working — GetService returns
        // null there and the filter degrades to "check every candidate",
        // matching pre-#745 behaviour.
        await using var walkScope = scopeFactory.CreateAsyncScope();
        var tenantGuard = walkScope.ServiceProvider.GetService<IUnitMembershipTenantGuard>();

        var parents = new List<Address>();
        foreach (var entry in all)
        {
            if (!string.Equals(entry.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (entry.Address == child)
            {
                continue;
            }

            // #745: filter cross-tenant candidates defensively. The
            // DirectoryService cache is shared across tenants today, so
            // ListAllAsync may return units the caller is not allowed to
            // see. The tenant guard uses tenant-scoped row visibility to
            // decide — a parent in another tenant is skipped before we
            // ever read its actor state.
            if (tenantGuard is not null
                && !await tenantGuard.ShareTenantAsync(entry.Address, child, ct))
            {
                continue;
            }

            Address[] members;
            try
            {
                var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                    new ActorId(entry.ActorId), nameof(UnitActor));
                members = await proxy.GetMembersAsync(ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Invalidate: failed to read members of {Unit}; skipping for parent walk.",
                    entry.Address);
                continue;
            }

            if (Array.Exists(members, m => m == child))
            {
                parents.Add(entry.Address);
            }
        }

        return parents;
    }

    private async Task<AggregatedExpertise> ComputeAsync(Address unit, CancellationToken ct)
    {
        // #745: open one scope for the whole walk so the scoped tenant
        // guard (and its SpringDbContext) is re-used across edges. The
        // aggregator itself is a singleton so the guard cannot be a ctor
        // dependency.
        await using var walkScope = scopeFactory.CreateAsyncScope();
        var tenantGuard = walkScope.ServiceProvider.GetService<IUnitMembershipTenantGuard>();

        if (!string.Equals(unit.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
        {
            // The aggregator is unit-oriented; for an agent, return whatever
            // the agent itself advertises, with the agent as its own origin.
            var agentDomains = await expertiseStore.GetDomainsAsync(unit, ct);
            var agentEntries = agentDomains
                .Select(d => new ExpertiseEntry(d, unit, new[] { unit }))
                .ToList();
            return new AggregatedExpertise(unit, agentEntries, 0, timeProvider.GetUtcNow());
        }

        var visited = new HashSet<string>(StringComparer.Ordinal)
        {
            ToKey(unit),
        };
        var queue = new Queue<(Address Current, IReadOnlyList<Address> Path)>();
        queue.Enqueue((unit, new[] { unit }));

        // We de-dup the output by (domain-name, origin-address) so two
        // different DAG paths to the same contributor don't double-count.
        var dedup = new Dictionary<string, ExpertiseEntry>(StringComparer.Ordinal);
        var maxDepth = 0;

        while (queue.Count > 0)
        {
            var (current, path) = queue.Dequeue();
            maxDepth = Math.Max(maxDepth, path.Count - 1);

            if (path.Count > MaxAggregationDepth)
            {
                throw new ExpertiseAggregationException(
                    unit,
                    path,
                    $"Aggregating expertise for '{unit}' exceeded max depth {MaxAggregationDepth}. Path: {DescribePath(path)}");
            }

            // Collect expertise from the current node.
            var domains = await expertiseStore.GetDomainsAsync(current, ct);
            foreach (var domain in domains)
            {
                var key = $"{domain.Name}|{current.Scheme}://{current.Path}";
                if (dedup.TryGetValue(key, out var existing))
                {
                    // Same (domain, origin) — keep the stronger level (or
                    // merge if levels disagree).
                    if (Stronger(domain.Level, existing.Domain.Level) == domain.Level)
                    {
                        dedup[key] = existing with { Domain = domain };
                    }
                    continue;
                }

                dedup[key] = new ExpertiseEntry(domain, current, path.ToArray());
            }

            // Units recurse into their members; agents are leaves.
            if (!string.Equals(current.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var members = await SafeReadMembersAsync(current, ct);
            foreach (var member in members)
            {
                // Cycle: a member that points back to the aggregating unit
                // is an operator error. The membership cycle check in
                // UnitActor.AddMemberAsync is supposed to prevent this, but
                // if state is corrupt we refuse to loop.
                if (member == unit)
                {
                    throw new ExpertiseAggregationException(
                        unit,
                        path.Append(member).ToList(),
                        $"Aggregation aborted: unit '{unit}' is (transitively) a member of itself via {DescribePath(path.Append(member).ToList())}");
                }

                if (!visited.Add(ToKey(member)))
                {
                    // Already visited — benign DAG convergence; skip.
                    continue;
                }

                // #745: even if actor state somehow holds a cross-tenant
                // reference (pre-guard row, direct DB edit, cloud-overlay
                // bypass), refuse to traverse it. The guard consults the
                // tenant-scoped row tables so a member whose tenant does
                // not match the current context is invisible — we skip it
                // and keep the rest of the walk going.
                if (tenantGuard is not null
                    && !await tenantGuard.ShareTenantAsync(current, member, ct))
                {
                    _logger.LogWarning(
                        "Aggregation: cross-tenant member {Member} of unit {Parent} skipped (tenant mismatch).",
                        member, current);
                    continue;
                }

                var nextPath = path.Append(member).ToList();
                queue.Enqueue((member, nextPath));
            }
        }

        var entries = dedup.Values
            .OrderBy(e => e.Domain.Name, StringComparer.Ordinal)
            .ThenBy(e => e.Origin.Path, StringComparer.Ordinal)
            .ToList();

        return new AggregatedExpertise(unit, entries, maxDepth, timeProvider.GetUtcNow());
    }

    private async Task<Address[]> SafeReadMembersAsync(Address unit, CancellationToken ct)
    {
        var entry = await SafeResolveAsync(unit, ct);
        if (entry is null)
        {
            return Array.Empty<Address>();
        }

        try
        {
            var proxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(entry.ActorId), nameof(UnitActor));
            return await proxy.GetMembersAsync(ct) ?? Array.Empty<Address>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Aggregation: failed to read members of {Unit}; treating as leaf.", unit);
            return Array.Empty<Address>();
        }
    }

    private async Task<DirectoryEntry?> SafeResolveAsync(Address address, CancellationToken ct)
    {
        try
        {
            return await directoryService.ResolveAsync(address, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Aggregation: directory resolve failed for {Address}; treating as unknown.", address);
            return null;
        }
    }

    private async Task<IReadOnlyList<UnitMembership>> ListMembershipsAsync(string agentPath, CancellationToken ct)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repo = scope.ServiceProvider.GetRequiredService<IUnitMembershipRepository>();
            return await repo.ListByAgentAsync(agentPath, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Invalidate: failed to list memberships for agent {Path}; stopping walk.",
                agentPath);
            return Array.Empty<UnitMembership>();
        }
    }

    private static ExpertiseLevel? Stronger(ExpertiseLevel? a, ExpertiseLevel? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return (int)a.Value >= (int)b.Value ? a : b;
    }

    private static string DescribePath(IReadOnlyList<Address> path) =>
        string.Join(" -> ", path.Select(a => $"{a.Scheme}://{a.Path}"));

    private static string ToKey(Address address) => $"{address.Scheme}://{address.Path}";

    /// <summary>
    /// Minimal <see cref="IServiceScopeFactory"/> adapter for the
    /// unit-test-only ctor that hands the aggregator a direct
    /// <see cref="IUnitMembershipRepository"/> instance. Production DI uses
    /// the real scoped provider; this adapter just returns the supplied
    /// repository from the simulated scope.
    /// </summary>
    private sealed class DirectRepositoryScopeFactory(IUnitMembershipRepository repo) : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new DirectScope(repo);

        private sealed class DirectScope(IUnitMembershipRepository repo) : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;

            public void Dispose()
            {
                // Nothing to dispose — the supplied repository outlives the scope.
            }

            public object? GetService(Type serviceType)
            {
                if (serviceType == typeof(IUnitMembershipRepository))
                {
                    return repo;
                }
                return null;
            }
        }
    }
}