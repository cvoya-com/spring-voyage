// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Units;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="IUnitMembershipCoordinator"/>.
/// Owns the membership-management concern for <c>UnitActor</c>: cycle
/// detection for <c>unit://</c>-typed members, idempotent edge
/// persistence through <see cref="IUnitMemberGraphStore"/>, and the
/// activity-event emission contract.
/// </summary>
/// <remarks>
/// <para>
/// Per #2052 / ADR-0040 the member graph lives in EF
/// (<c>unit_memberships</c> + <c>unit_subunit_memberships</c>). The
/// coordinator never touches actor state — both the cycle-detection walk
/// and the write go through <see cref="IUnitMemberGraphStore"/> against
/// the same tenant-scoped tables.
/// </para>
/// <para>
/// The coordinator is stateless with respect to any individual unit, so
/// it is safe to register as a singleton and share across all
/// <c>UnitActor</c> instances.
/// </para>
/// </remarks>
public class UnitMembershipCoordinator(
    IUnitMemberGraphStore graphStore,
    ILogger<UnitMembershipCoordinator> logger) : IUnitMembershipCoordinator
{
    /// <summary>
    /// Maximum number of levels walked during cycle detection before the
    /// walk is treated as itself a cycle signal. Keeps
    /// <see cref="AddMemberAsync"/> bounded even in the face of
    /// pathological graphs.
    /// </summary>
    internal const int MaxCycleDetectionDepth = 64;

    /// <inheritdoc />
    public async Task AddMemberAsync(
        Guid unitId,
        Address unitAddress,
        Address member,
        Func<Address, int, CancellationToken, Task> emitStateChanged,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unitAddress);
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(emitStateChanged);

        var isUnitMember = string.Equals(member.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase);
        var isAgentMember = string.Equals(member.Scheme, Address.AgentScheme, StringComparison.OrdinalIgnoreCase);

        if (!isUnitMember && !isAgentMember)
        {
            throw new ArgumentException(
                $"Unsupported member scheme '{member.Scheme}' (only 'agent' and 'unit' are valid).",
                nameof(member));
        }

        // Cycle detection only applies to unit-typed members — agents are
        // leaves and cannot introduce a containment cycle.
        if (isUnitMember)
        {
            await EnsureNoCycleAsync(unitId, unitAddress, member, cancellationToken);
        }

        var inserted = isAgentMember
            ? await graphStore.AddAgentMemberAsync(unitId, member.Id, cancellationToken)
            : await graphStore.AddSubunitMemberAsync(unitId, member.Id, cancellationToken);

        if (!inserted)
        {
            logger.LogWarning(
                "Unit {UnitId} already contains member {Member}; idempotent add ignored.",
                unitId, member);
            return;
        }

        var totalMembers = (await graphStore.GetMembersAsync(unitId, cancellationToken)).Count;

        logger.LogInformation(
            "Unit {UnitId} added member {Member}. Total members: {Count}",
            unitId, member, totalMembers);

        await emitStateChanged(member, totalMembers, cancellationToken);
    }

    /// <inheritdoc />
    public async Task RemoveMemberAsync(
        Guid unitId,
        Address member,
        Func<Address, int, CancellationToken, Task> emitStateChanged,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(emitStateChanged);

        var removed = string.Equals(member.Scheme, Address.AgentScheme, StringComparison.OrdinalIgnoreCase)
            ? await graphStore.RemoveAgentMemberAsync(unitId, member.Id, cancellationToken)
            : string.Equals(member.Scheme, Address.UnitScheme, StringComparison.OrdinalIgnoreCase)
                ? await graphStore.RemoveSubunitMemberAsync(unitId, member.Id, cancellationToken)
                : false;

        if (!removed)
        {
            logger.LogWarning(
                "Unit {UnitId} does not contain member {Member}; idempotent remove ignored.",
                unitId, member);
            return;
        }

        var totalMembers = (await graphStore.GetMembersAsync(unitId, cancellationToken)).Count;

        logger.LogInformation(
            "Unit {UnitId} removed member {Member}. Total members: {Count}",
            unitId, member, totalMembers);

        await emitStateChanged(member, totalMembers, cancellationToken);
    }

    /// <summary>
    /// Verifies that adding <paramref name="candidate"/> as a
    /// <c>unit://</c> member of the unit identified by
    /// <paramref name="unitId"/> would not introduce a cycle. Throws
    /// <see cref="CyclicMembershipException"/> on self-loop, back-edge, or
    /// when the walk exceeds <see cref="MaxCycleDetectionDepth"/>. Reads
    /// the sub-unit graph through <see cref="IUnitMemberGraphStore"/> so
    /// the walk runs against EF, not actor state.
    /// </summary>
    private async Task EnsureNoCycleAsync(
        Guid unitId,
        Address unitAddress,
        Address candidate,
        CancellationToken cancellationToken)
    {
        // Fast self-loop check by Guid identity. Address equality is the
        // canonical signal post-#1629 — both sides carry the same Guid.
        if (candidate.Id == unitId)
        {
            throw BuildCycleException(unitAddress, candidate, [candidate],
                $"Unit '{unitAddress}' cannot be added as a member of itself.");
        }

        // BFS the candidate's sub-unit graph. Whenever we land on the
        // parent unit's id, a cycle exists and we must reject the add.
        var visited = new HashSet<Guid> { candidate.Id };
        var queue = new Queue<(Guid Current, IReadOnlyList<Address> Path)>();
        queue.Enqueue((candidate.Id, new[] { candidate }));

        while (queue.Count > 0)
        {
            var (current, path) = queue.Dequeue();

            if (path.Count > MaxCycleDetectionDepth)
            {
                logger.LogWarning(
                    "Unit {UnitId} rejected adding member {Candidate}: cycle-detection walk exceeded max depth {MaxDepth}. Path: {Path}",
                    unitId, candidate, MaxCycleDetectionDepth, DescribePath(path));

                throw BuildCycleException(unitAddress, candidate, path,
                    $"Adding '{candidate}' to unit '{unitAddress}' would exceed the maximum unit-nesting depth ({MaxCycleDetectionDepth}). Treating as a cycle.");
            }

            IReadOnlyList<Guid> children;
            try
            {
                children = await graphStore.ListDirectSubunitChildrenAsync(current, cancellationToken);
            }
            catch (Exception ex) when (ex is not SpringException && ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Unit {UnitId} cycle-check: failed to read sub-unit children of {Current}; treating as dead end.",
                    unitId, current);
                continue;
            }

            foreach (var childId in children)
            {
                if (childId == unitId)
                {
                    var childAddress = new Address(Address.UnitScheme, childId);
                    var cyclePath = path.Append(childAddress).ToList();

                    logger.LogWarning(
                        "Unit {UnitId} rejected adding member {Candidate}: cycle detected. Path: {Path}",
                        unitId, candidate, DescribePath(cyclePath));

                    throw BuildCycleException(unitAddress, candidate, cyclePath,
                        $"Adding '{candidate}' to unit '{unitAddress}' would create a membership cycle: {DescribePath(cyclePath)}.");
                }

                if (!visited.Add(childId))
                {
                    continue;
                }

                var childAddr = new Address(Address.UnitScheme, childId);
                queue.Enqueue((childId, path.Append(childAddr).ToList()));
            }
        }
    }

    private static string DescribePath(IReadOnlyList<Address> path) =>
        string.Join(" -> ", path.Select(a => $"{a.Scheme}://{a.Path}"));

    private static CyclicMembershipException BuildCycleException(
        Address parent, Address candidate, IReadOnlyList<Address> path, string message) =>
        new(parent, candidate, path, message);
}
