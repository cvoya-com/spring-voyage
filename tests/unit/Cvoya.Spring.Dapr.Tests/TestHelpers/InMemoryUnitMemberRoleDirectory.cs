// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.TestHelpers;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Units;

/// <summary>
/// In-memory test double for <see cref="IUnitMemberRoleDirectory"/> (#3089).
/// Mirrors the production join semantics — agent-member effective roles are
/// <c>membership roles ∪ agent_definitions.role</c> via
/// <see cref="EffectiveRolePolicy.Combine"/> — without standing up a real
/// <c>SpringDbContext</c>. The cross-restart / real-EF behaviour is covered
/// by the integration tests. Tests seed the per-(unit, agent) membership
/// roles and the per-agent definition role separately, matching the two
/// sources the production seam joins.
/// </summary>
public sealed class InMemoryUnitMemberRoleDirectory : IUnitMemberRoleDirectory
{
    private readonly ConcurrentDictionary<(Guid Unit, Guid Agent), IReadOnlyList<string>> _membershipRoles = new();
    private readonly ConcurrentDictionary<Guid, string?> _agentDefinitionRoles = new();

    /// <summary>
    /// Seeds an agent's membership in a unit with its per-membership role
    /// labels (the <c>unit_memberships.roles</c> source) and, optionally,
    /// its definition-level role (the <c>agent_definitions.role</c> source).
    /// </summary>
    public InMemoryUnitMemberRoleDirectory Seed(
        Guid unitId,
        Guid agentId,
        IReadOnlyList<string>? membershipRoles = null,
        string? agentDefinitionRole = null)
    {
        _membershipRoles[(unitId, agentId)] = membershipRoles ?? Array.Empty<string>();
        if (agentDefinitionRole is not null)
        {
            _agentDefinitionRoles[agentId] = agentDefinitionRole;
        }
        return this;
    }

    /// <summary>
    /// Seeds (or overrides) only an agent's definition-level role, for the
    /// case where the agent was first seeded as a bare member.
    /// </summary>
    public InMemoryUnitMemberRoleDirectory SeedAgentDefinitionRole(Guid agentId, string? role)
    {
        _agentDefinitionRoles[agentId] = role;
        return this;
    }

    /// <inheritdoc />
    public Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetAgentMemberRolesAsync(
        Guid unitId,
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, IReadOnlyList<string>>();
        foreach (var ((unit, agent), membershipRoles) in _membershipRoles)
        {
            if (unit != unitId)
            {
                continue;
            }
            _agentDefinitionRoles.TryGetValue(agent, out var definitionRole);
            var effective = EffectiveRolePolicy.Combine(membershipRoles, definitionRole);
            if (effective.Count > 0)
            {
                result[agent] = effective;
            }
        }
        return Task.FromResult<IReadOnlyDictionary<Guid, IReadOnlyList<string>>>(result);
    }
}
