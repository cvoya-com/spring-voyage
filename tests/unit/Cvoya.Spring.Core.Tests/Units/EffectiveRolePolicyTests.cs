// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests.Units;

using System;

using Cvoya.Spring.Core.Units;

using Shouldly;

using Xunit;

/// <summary>
/// Pins the single effective-roles rule (#3089 / #3088): an agent member's
/// effective roles are <c>membership roles ∪ agent_definitions.role</c> —
/// membership roles first in order, then the definition role appended iff
/// not already present (case-insensitive), with whitespace-only entries
/// dropped. Every directory surface and the DB-backed member-role seam
/// share this one rule.
/// </summary>
public sealed class EffectiveRolePolicyTests
{
    [Fact]
    public void Combine_MembershipRolesOnly_ReturnsMembershipRolesInOrder()
    {
        var result = EffectiveRolePolicy.Combine(new[] { "owner", "reviewer" }, agentRole: null);

        result.ShouldBe(new[] { "owner", "reviewer" });
    }

    [Fact]
    public void Combine_DefinitionRoleOnly_ReturnsDefinitionRole()
    {
        // The agent-by-reference case: no membership roles, role lives on
        // the agent definition.
        var result = EffectiveRolePolicy.Combine(membershipRoles: null, agentRole: "staff-writer");

        result.ShouldBe(new[] { "staff-writer" });
    }

    [Fact]
    public void Combine_BothSources_AppendsDefinitionRoleAfterMembershipRoles()
    {
        var result = EffectiveRolePolicy.Combine(new[] { "owner" }, agentRole: "managing-editor");

        result.ShouldBe(new[] { "owner", "managing-editor" });
    }

    [Fact]
    public void Combine_DefinitionRoleAlreadyInMembership_DedupesCaseInsensitively()
    {
        var result = EffectiveRolePolicy.Combine(new[] { "Staff-Writer" }, agentRole: "staff-writer");

        // The membership entry keeps its original casing and the definition
        // role is not appended a second time.
        result.ShouldBe(new[] { "Staff-Writer" });
    }

    [Fact]
    public void Combine_TrimsAndDropsWhitespaceEntries()
    {
        var result = EffectiveRolePolicy.Combine(
            new[] { "  owner  ", "   ", "" },
            agentRole: "  reviewer  ");

        result.ShouldBe(new[] { "owner", "reviewer" });
    }

    [Fact]
    public void Combine_BothEmpty_ReturnsEmpty()
    {
        EffectiveRolePolicy.Combine(membershipRoles: null, agentRole: null).ShouldBeEmpty();
        EffectiveRolePolicy.Combine(Array.Empty<string>(), agentRole: "   ").ShouldBeEmpty();
    }
}
