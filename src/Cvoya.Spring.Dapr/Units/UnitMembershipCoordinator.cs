// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Units;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="IUnitMembershipCoordinator"/>.
/// Owns the membership-management concern extracted from <c>UnitActor</c>:
/// duplicate detection on add, BFS cycle-detection for <c>unit://</c>-typed
/// members, state persistence via caller-supplied delegates, and mirroring
/// every <c>unit://</c> mutation into the persistent subunit projection table
/// via an optional <see cref="IUnitSubunitMembershipProjector"/>.
/// </summary>
/// <remarks>
/// The coordinator is stateless with respect to any individual unit — it
/// operates entirely through the per-call delegates and the injected
/// singletons. This makes it safe to register as a singleton and share
/// across all <c>UnitActor</c> instances.
/// </remarks>
public class UnitMembershipCoordinator(
    IUnitSubunitMembershipProjector? subunitProjector,
    ILogger<UnitMembershipCoordinator> logger) : IUnitMembershipCoordinator
{
    /// <summary>
    /// Maximum number of levels walked during cycle detection before the walk
    /// is treated as itself a cycle signal. Keeps <see cref="AddMemberAsync"/>
    /// bounded even in the face of pathological graphs.
    /// </summary>
    internal const int MaxCycleDetectionDepth = 64;

    /// <inheritdoc />
    public async Task AddMemberAsync(
        string unitActorId,
        Address unitAddress,
        Address member,
        Func<CancellationToken, Task<List<Address>>> getMembers,
        Func<List<Address>, CancellationToken, Task> persistMembers,
        Func<Address, CancellationToken, Task<DirectoryEntry?>> resolveAddress,
        Func<string, CancellationToken, Task<Address[]>> getSubUnitMembers,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);

        var members = await getMembers(cancellationToken);

        if (members.Exists(m => m == member))
        {
            logger.LogWarning(
                "Unit {ActorId} already contains member {Member}", unitActorId, member);
            return;
        }

        // Cycle detection only applies to unit-typed members — agents can
        // belong to at most one unit (1:N parent) and are leaves, so they
        // cannot introduce a cycle in the containment graph.
        if (string.Equals(member.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
        {
            await EnsureNoCycleAsync(
                unitActorId, unitAddress, member, resolveAddress, getSubUnitMembers, cancellationToken);
        }

        members.Add(member);
        await persistMembers(members, cancellationToken);

        logger.LogInformation(
            "Unit {ActorId} added member {Member}. Total members: {Count}",
            unitActorId, member, members.Count);

        // #1154: persist the parent → child edge so the tenant-tree
        // endpoint can resolve nested unit hierarchies without a
        // per-unit actor fanout. The projector swallows its own
        // failures — the actor-state write above is authoritative and
        // the startup reconciliation service replays drifted edges on
        // the next host boot.
        if (subunitProjector is not null
            && string.Equals(member.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
        {
            await subunitProjector.ProjectAddAsync(unitActorId, member.Path, cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task RemoveMemberAsync(
        string unitActorId,
        Address member,
        Func<CancellationToken, Task<List<Address>>> getMembers,
        Func<List<Address>, CancellationToken, Task> persistMembers,
        CancellationToken cancellationToken = default)
    {
        var members = await getMembers(cancellationToken);
        var removed = members.RemoveAll(m => m == member);

        if (removed == 0)
        {
            logger.LogWarning(
                "Unit {ActorId} does not contain member {Member}", unitActorId, member);
            return;
        }

        await persistMembers(members, cancellationToken);

        logger.LogInformation(
            "Unit {ActorId} removed member {Member}. Total members: {Count}",
            unitActorId, member, members.Count);

        // #1154: keep the persistent projection in sync with the
        // actor-state list. Failures are swallowed by the projector;
        // the next host start reconciles any drift.
        if (subunitProjector is not null
            && string.Equals(member.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
        {
            await subunitProjector.ProjectRemoveAsync(unitActorId, member.Path, cancellationToken);
        }
    }

    /// <summary>
    /// Verifies that adding <paramref name="candidate"/> as a <c>unit://</c>
    /// member of the unit identified by <paramref name="unitActorId"/> would
    /// not introduce a cycle. Throws <see cref="CyclicMembershipException"/>
    /// on self-loop, back-edge, or when the walk exceeds
    /// <see cref="MaxCycleDetectionDepth"/>.
    /// <para>
    /// The walk resolves each candidate path to its backing actor id via the
    /// <paramref name="resolveAddress"/> delegate, then reads its current
    /// members via <paramref name="getSubUnitMembers"/>. Missing or non-unit
    /// members are treated as dead ends — they cannot close a cycle.
    /// </para>
    /// </summary>
    private async Task EnsureNoCycleAsync(
        string unitActorId,
        Address unitAddress,
        Address candidate,
        Func<Address, CancellationToken, Task<DirectoryEntry?>> resolveAddress,
        Func<string, CancellationToken, Task<Address[]>> getSubUnitMembers,
        CancellationToken cancellationToken)
    {
        // Fast self-loop check: candidate resolves (by address equality) to
        // this same actor. Works even if the candidate was addressed via
        // path-form rather than actor-id form — the path-form case is caught
        // one level below after directory resolution.
        if (candidate == unitAddress)
        {
            throw BuildCycleException(unitAddress, candidate, [candidate],
                $"Unit '{unitAddress}' cannot be added as a member of itself.");
        }

        // Walk the candidate's sub-unit graph breadth-first. Whenever we
        // land on an actor whose id matches this unit's actor id, a cycle
        // exists and we must reject the add.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(Address Unit, IReadOnlyList<Address> PathFromCandidate)>();
        queue.Enqueue((candidate, [candidate]));

        while (queue.Count > 0)
        {
            var (current, pathFromCandidate) = queue.Dequeue();

            if (pathFromCandidate.Count > MaxCycleDetectionDepth)
            {
                logger.LogWarning(
                    "Unit {ActorId} rejected adding member {Candidate}: cycle-detection walk exceeded max depth {MaxDepth}. Path: {Path}",
                    unitActorId, candidate, MaxCycleDetectionDepth, DescribePath(pathFromCandidate));

                throw BuildCycleException(unitAddress, candidate, pathFromCandidate,
                    $"Adding '{candidate}' to unit '{unitAddress}' would exceed the maximum unit-nesting depth ({MaxCycleDetectionDepth}). Treating as a cycle.");
            }

            DirectoryEntry? entry;
            try
            {
                entry = await resolveAddress(current, cancellationToken);
            }
            catch (Exception ex) when (ex is not SpringException)
            {
                // Directory read failures during traversal should not poison
                // the add — they look like "unreachable" and surface as a
                // log-worthy warning, not a cycle.
                logger.LogWarning(ex,
                    "Unit {ActorId} cycle-check: failed to resolve {Unit}; treating as dead end.",
                    unitActorId, current);
                continue;
            }

            if (entry is null)
            {
                // Unknown unit — not a cycle via this path.
                continue;
            }

            // Back-edge check: did we just land on this unit?
            if (string.Equals(entry.ActorId, unitActorId, StringComparison.Ordinal))
            {
                var cyclePath = pathFromCandidate.Append(unitAddress).ToList();

                logger.LogWarning(
                    "Unit {ActorId} rejected adding member {Candidate}: cycle detected. Path: {Path}",
                    unitActorId, candidate, DescribePath(cyclePath));

                throw BuildCycleException(unitAddress, candidate, cyclePath,
                    $"Adding '{candidate}' to unit '{unitAddress}' would create a membership cycle: {DescribePath(cyclePath)}.");
            }

            // Mark this unit as visited by actor id so different address
            // spellings (e.g. path-form and uuid-form of the same unit) are
            // coalesced and we cannot get stuck on a benign sub-graph cycle
            // that does not involve this unit.
            if (!visited.Add(entry.ActorId))
            {
                continue;
            }

            Address[] subMembers;
            try
            {
                subMembers = await getSubUnitMembers(entry.ActorId, cancellationToken);
            }
            catch (Exception ex) when (ex is not SpringException)
            {
                // If the sub-unit is deleted or otherwise unreachable mid-walk,
                // treat as "not a cycle via that path" and continue.
                logger.LogWarning(ex,
                    "Unit {ActorId} cycle-check: failed to read members of {Unit} (actorId={SubActorId}); treating as dead end.",
                    unitActorId, current, entry.ActorId);
                continue;
            }

            foreach (var sub in subMembers)
            {
                if (!string.Equals(sub.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var nextPath = pathFromCandidate.Append(sub).ToList();
                queue.Enqueue((sub, nextPath));
            }
        }
    }

    private static string DescribePath(IReadOnlyList<Address> path) =>
        string.Join(" -> ", path.Select(a => $"{a.Scheme}://{a.Path}"));

    private static CyclicMembershipException BuildCycleException(
        Address parent, Address candidate, IReadOnlyList<Address> path, string message) =>
        new(parent, candidate, path, message);
}