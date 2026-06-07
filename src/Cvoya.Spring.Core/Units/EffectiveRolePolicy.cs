// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using System;
using System.Collections.Generic;

/// <summary>
/// The single definition of an agent member's <em>effective</em> roles
/// (#3089 / #3088). An agent member's role comes from two sources: the
/// free-form per-membership labels on the <c>unit_memberships</c> row, and
/// the agent's own definition-level role (<c>agent_definitions.role</c>).
/// The effective set is their union — membership roles first in their
/// existing order, then the definition role appended iff it is not already
/// present (case-insensitive dedupe).
/// <para>
/// Extracted to <c>Cvoya.Spring.Core</c> so every directory surface and the
/// DB-backed <see cref="IUnitMemberRoleDirectory"/> share one rule rather
/// than each re-implementing the fold (the drift #3089 set out to remove).
/// </para>
/// </summary>
public static class EffectiveRolePolicy
{
    /// <summary>
    /// Combines an agent's per-membership <paramref name="membershipRoles"/>
    /// with its definition-level <paramref name="agentRole"/>. Whitespace-
    /// only entries are dropped and surviving entries are trimmed. The
    /// definition role is appended only when no membership role already
    /// matches it case-insensitively; the membership roles keep their order
    /// and original casing.
    /// </summary>
    /// <param name="membershipRoles">
    /// The free-form per-membership role labels (may be <see langword="null"/>
    /// or empty).
    /// </param>
    /// <param name="agentRole">
    /// The agent's definition-level role (may be <see langword="null"/> /
    /// blank for non-agent members or agents without a role).
    /// </param>
    /// <returns>
    /// The effective roles, in stable order. Empty when both sources are
    /// empty.
    /// </returns>
    public static IReadOnlyList<string> Combine(
        IReadOnlyList<string>? membershipRoles,
        string? agentRole)
    {
        var result = new List<string>();
        if (membershipRoles is not null)
        {
            foreach (var role in membershipRoles)
            {
                if (string.IsNullOrWhiteSpace(role))
                {
                    continue;
                }
                result.Add(role.Trim());
            }
        }

        if (string.IsNullOrWhiteSpace(agentRole))
        {
            return result;
        }

        var trimmedAgentRole = agentRole.Trim();
        foreach (var role in result)
        {
            if (string.Equals(role, trimmedAgentRole, StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }
        }

        result.Add(trimmedAgentRole);
        return result;
    }
}
