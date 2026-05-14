// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.TestHelpers;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;

/// <summary>
/// In-memory test double for <see cref="IUnitMemberGraphStore"/>. Lets
/// unit tests exercise the EF-backed member graph (#2052 / ADR-0040)
/// without standing up a Postgres / Testcontainer. Cross-restart
/// behaviour is covered by the integration tests with a real
/// <c>SpringDbContext</c>.
/// </summary>
public class InMemoryUnitMemberGraphStore : IUnitMemberGraphStore
{
    private readonly ConcurrentDictionary<Guid, HashSet<Guid>> _agentMembers = new();
    private readonly ConcurrentDictionary<Guid, HashSet<Guid>> _subunitChildren = new();

    public Task<IReadOnlyList<Address>> GetMembersAsync(
        Guid unitId, CancellationToken cancellationToken = default)
    {
        var members = new List<Address>();
        if (_agentMembers.TryGetValue(unitId, out var agents))
        {
            foreach (var a in agents.OrderBy(g => g))
            {
                members.Add(new Address(Address.AgentScheme, a));
            }
        }
        if (_subunitChildren.TryGetValue(unitId, out var subs))
        {
            foreach (var c in subs.OrderBy(g => g))
            {
                members.Add(new Address(Address.UnitScheme, c));
            }
        }
        return Task.FromResult<IReadOnlyList<Address>>(members);
    }

    public Task<bool> AddAgentMemberAsync(
        Guid unitId, Guid agentId, CancellationToken cancellationToken = default)
    {
        var set = _agentMembers.GetOrAdd(unitId, _ => []);
        lock (set)
        {
            return Task.FromResult(set.Add(agentId));
        }
    }

    public Task<bool> AddSubunitMemberAsync(
        Guid parentId, Guid childId, CancellationToken cancellationToken = default)
    {
        var set = _subunitChildren.GetOrAdd(parentId, _ => []);
        lock (set)
        {
            return Task.FromResult(set.Add(childId));
        }
    }

    public Task<bool> RemoveAgentMemberAsync(
        Guid unitId, Guid agentId, CancellationToken cancellationToken = default)
    {
        if (!_agentMembers.TryGetValue(unitId, out var set))
        {
            return Task.FromResult(false);
        }
        lock (set)
        {
            return Task.FromResult(set.Remove(agentId));
        }
    }

    public Task<bool> RemoveSubunitMemberAsync(
        Guid parentId, Guid childId, CancellationToken cancellationToken = default)
    {
        if (!_subunitChildren.TryGetValue(parentId, out var set))
        {
            return Task.FromResult(false);
        }
        lock (set)
        {
            return Task.FromResult(set.Remove(childId));
        }
    }

    public Task<IReadOnlyList<Guid>> ListDirectSubunitChildrenAsync(
        Guid parentId, CancellationToken cancellationToken = default)
    {
        if (!_subunitChildren.TryGetValue(parentId, out var set))
        {
            return Task.FromResult<IReadOnlyList<Guid>>(Array.Empty<Guid>());
        }
        lock (set)
        {
            return Task.FromResult<IReadOnlyList<Guid>>(set.OrderBy(g => g).ToList());
        }
    }

    public Task EnsureTopLevelEdgeAsync(
        Guid unitId, Guid tenantId, CancellationToken cancellationToken = default)
    {
        return AddSubunitMemberAsync(tenantId, unitId, cancellationToken);
    }

    /// <summary>
    /// Test convenience: seed the unit's agent member set without going
    /// through the public surface. Mirrors the in-memory shape used by
    /// other test stores in this folder.
    /// </summary>
    public void SeedAgentMembers(Guid unitId, params Guid[] agentIds)
    {
        var set = _agentMembers.GetOrAdd(unitId, _ => []);
        lock (set)
        {
            foreach (var id in agentIds)
            {
                set.Add(id);
            }
        }
    }

    /// <summary>
    /// Test convenience: seed the unit's sub-unit edges directly.
    /// </summary>
    public void SeedSubunitChildren(Guid parentId, params Guid[] childIds)
    {
        var set = _subunitChildren.GetOrAdd(parentId, _ => []);
        lock (set)
        {
            foreach (var id in childIds)
            {
                set.Add(id);
            }
        }
    }
}
