// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Issues;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Issues;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Default <see cref="IIssueAggregator"/> implementation (#2160). Walks
/// the unit member graph via <see cref="IUnitMemberGraphStore"/>,
/// classifies edges as units or agents by scheme, and batches the full
/// descendant set through <see cref="IIssueReader.CountOpenAsync"/> in
/// a single round-trip.
/// </summary>
public class IssueAggregator : IIssueAggregator
{
    private readonly IIssueReader _reader;
    private readonly IUnitMemberGraphStore _memberGraph;
    private readonly IServiceScopeFactory _scopeFactory;

    /// <summary>Initialises a new <see cref="IssueAggregator"/>.</summary>
    public IssueAggregator(
        IIssueReader reader,
        IUnitMemberGraphStore memberGraph,
        IServiceScopeFactory scopeFactory)
    {
        _reader = reader;
        _memberGraph = memberGraph;
        _scopeFactory = scopeFactory;
    }

    /// <inheritdoc />
    public async Task<IssuesView> AggregateForUnitAsync(
        Guid unitId, CancellationToken cancellationToken = default)
    {
        var subject = new IssueSubject(IssueSubjectKind.Unit, unitId);
        var own = await _reader.ListOpenAsync(subject, cancellationToken);

        // Walk the member graph BFS. Children are the immediate members;
        // descendants are the full closure. The visited set prevents
        // cycles (defensive — sub-unit edges are a DAG by validation).
        var immediateChildren = new List<IssueSubject>();
        var allDescendants = new List<IssueSubject>();
        var visited = new HashSet<IssueSubject>();

        var directMembers = await _memberGraph.GetMembersAsync(unitId, cancellationToken);
        foreach (var addr in directMembers)
        {
            var child = MemberToSubject(addr);
            if (child is null || !visited.Add(child)) continue;
            immediateChildren.Add(child);
            allDescendants.Add(child);
        }

        var queue = new Queue<IssueSubject>(immediateChildren.Where(c => c.Kind == IssueSubjectKind.Unit));
        while (queue.Count > 0)
        {
            var unit = queue.Dequeue();
            var members = await _memberGraph.GetMembersAsync(unit.Id, cancellationToken);
            foreach (var addr in members)
            {
                var grandchild = MemberToSubject(addr);
                if (grandchild is null || !visited.Add(grandchild)) continue;
                allDescendants.Add(grandchild);
                if (grandchild.Kind == IssueSubjectKind.Unit)
                {
                    queue.Enqueue(grandchild);
                }
            }
        }

        var counts = await _reader.CountOpenAsync(allDescendants, cancellationToken);
        var rollup = await BuildDescendantRollupAsync(
            immediateChildren, allDescendants, counts, cancellationToken);

        return new IssuesView(own, rollup);
    }

    /// <inheritdoc />
    public async Task<IssuesView> AggregateForAgentAsync(
        Guid agentId, CancellationToken cancellationToken = default)
    {
        var subject = new IssueSubject(IssueSubjectKind.Agent, agentId);
        var own = await _reader.ListOpenAsync(subject, cancellationToken);
        return new IssuesView(own, new IssueDescendantRollup(0, 0, Array.Empty<IssueChildSummary>()));
    }

    private static IssueSubject? MemberToSubject(Address addr) => addr.Scheme switch
    {
        Address.UnitScheme => new IssueSubject(IssueSubjectKind.Unit, addr.Id),
        Address.AgentScheme => new IssueSubject(IssueSubjectKind.Agent, addr.Id),
        _ => null,
    };

    private async Task<IssueDescendantRollup> BuildDescendantRollupAsync(
        IReadOnlyList<IssueSubject> immediateChildren,
        IReadOnlyList<IssueSubject> allDescendants,
        IReadOnlyDictionary<IssueSubject, IssueCounts> counts,
        CancellationToken ct)
    {
        var totalErrors = counts.Values.Sum(c => c.ErrorCount);
        var totalWarnings = counts.Values.Sum(c => c.WarningCount);

        if (immediateChildren.Count == 0)
        {
            return new IssueDescendantRollup(totalErrors, totalWarnings, Array.Empty<IssueChildSummary>());
        }

        // For per-child summaries we sum each immediate child's own
        // counts plus the counts of every descendant *of that child*.
        // Build a child → descendants map to avoid an O(N²) scan.
        var descendantsByChild = new Dictionary<IssueSubject, List<IssueSubject>>();
        foreach (var child in immediateChildren)
        {
            descendantsByChild[child] = new List<IssueSubject>();
        }

        // For agents, descendants are empty. For units, walk again so we
        // associate every grandchild with the immediate-child branch it
        // came from. Cheap second walk — we already loaded the data.
        foreach (var child in immediateChildren.Where(c => c.Kind == IssueSubjectKind.Unit))
        {
            var seen = new HashSet<IssueSubject>();
            var queue = new Queue<IssueSubject>();
            var members = await _memberGraph.GetMembersAsync(child.Id, ct);
            foreach (var addr in members)
            {
                var node = MemberToSubject(addr);
                if (node is null || !seen.Add(node)) continue;
                descendantsByChild[child].Add(node);
                if (node.Kind == IssueSubjectKind.Unit)
                {
                    queue.Enqueue(node);
                }
            }
            while (queue.Count > 0)
            {
                var unit = queue.Dequeue();
                var grand = await _memberGraph.GetMembersAsync(unit.Id, ct);
                foreach (var addr in grand)
                {
                    var node = MemberToSubject(addr);
                    if (node is null || !seen.Add(node)) continue;
                    descendantsByChild[child].Add(node);
                    if (node.Kind == IssueSubjectKind.Unit)
                    {
                        queue.Enqueue(node);
                    }
                }
            }
        }

        // Fetch display names for every immediate child in one go.
        var nameLookup = await ResolveDisplayNamesAsync(immediateChildren, ct);

        var byChild = new List<IssueChildSummary>(immediateChildren.Count);
        foreach (var child in immediateChildren)
        {
            int errs = 0, warns = 0;
            if (counts.TryGetValue(child, out var ownCounts))
            {
                errs += ownCounts.ErrorCount;
                warns += ownCounts.WarningCount;
            }
            foreach (var desc in descendantsByChild[child])
            {
                if (counts.TryGetValue(desc, out var c))
                {
                    errs += c.ErrorCount;
                    warns += c.WarningCount;
                }
            }
            if (errs == 0 && warns == 0) continue; // omit clean children

            var name = nameLookup.TryGetValue(child, out var n) ? n : "(unknown)";
            byChild.Add(new IssueChildSummary(child, name, errs, warns));
        }

        return new IssueDescendantRollup(totalErrors, totalWarnings, byChild);
    }

    private async Task<IReadOnlyDictionary<IssueSubject, string>> ResolveDisplayNamesAsync(
        IReadOnlyList<IssueSubject> subjects,
        CancellationToken ct)
    {
        if (subjects.Count == 0)
        {
            return new Dictionary<IssueSubject, string>();
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

        var unitIds = subjects.Where(s => s.Kind == IssueSubjectKind.Unit).Select(s => s.Id).ToList();
        var agentIds = subjects.Where(s => s.Kind == IssueSubjectKind.Agent).Select(s => s.Id).ToList();

        var result = new Dictionary<IssueSubject, string>();
        if (unitIds.Count > 0)
        {
            var rows = await db.UnitDefinitions
                .AsNoTracking()
                .Where(u => unitIds.Contains(u.Id))
                .Select(u => new { u.Id, u.DisplayName })
                .ToListAsync(ct);
            foreach (var r in rows)
            {
                result[new IssueSubject(IssueSubjectKind.Unit, r.Id)] = r.DisplayName ?? string.Empty;
            }
        }
        if (agentIds.Count > 0)
        {
            var rows = await db.AgentDefinitions
                .AsNoTracking()
                .Where(a => agentIds.Contains(a.Id))
                .Select(a => new { a.Id, a.DisplayName })
                .ToListAsync(ct);
            foreach (var r in rows)
            {
                result[new IssueSubject(IssueSubjectKind.Agent, r.Id)] = r.DisplayName ?? string.Empty;
            }
        }
        return result;
    }
}
