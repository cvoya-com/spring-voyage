// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Units;

using System;
using System.Collections.Generic;
using System.Linq;

using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Tenancy;
using Cvoya.Spring.Dapr.Units;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for the single DB-backed member-role seam
/// <see cref="UnitMemberRoleDirectory"/> (#3089). Pin that the one join over
/// <c>unit_memberships</c> ⨝ <c>agent_definitions</c> resolves effective
/// roles correctly from BOTH sources the member model has by design:
/// <list type="bullet">
///   <item><description>agent-by-reference role on <c>agent_definitions.role</c>;</description></item>
///   <item><description>free-form per-membership roles on the <c>unit_memberships</c> row;</description></item>
///   <item><description>their deduped union when both are present.</description></item>
/// </list>
/// </summary>
public sealed class UnitMemberRoleDirectoryTests
{
    private static readonly Guid TenantId =
        Guid.Parse("dd55c4ea-8d72-5e43-a9df-88d07af02b69");
    private static readonly Guid UnitId =
        Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid OtherUnitId =
        Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid StaffWriter =
        Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid FactChecker =
        Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid Owner =
        Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    [Fact]
    public async Task GetAgentMemberRoles_AgentByReference_SurfacesDefinitionRole()
    {
        // The magazine model: unit_memberships.roles is empty; the role
        // lives on agent_definitions.role. The seam must still surface it.
        var fixture = new Fixture()
            .WithAgent(StaffWriter, role: "staff-writer")
            .WithMembership(UnitId, StaffWriter, membershipRoles: Array.Empty<string>())
            .Build();

        var roles = await fixture.GetAgentMemberRolesAsync(UnitId);

        roles.ShouldContainKey(StaffWriter);
        roles[StaffWriter].ShouldBe(new[] { "staff-writer" });
    }

    [Fact]
    public async Task GetAgentMemberRoles_MembershipRolesOnly_SurfacesMembershipRoles()
    {
        // The other source: the role lives on the unit_memberships row and
        // the agent definition carries no role.
        var fixture = new Fixture()
            .WithAgent(Owner, role: null)
            .WithMembership(UnitId, Owner, membershipRoles: new[] { "owner", "reviewer" })
            .Build();

        var roles = await fixture.GetAgentMemberRolesAsync(UnitId);

        roles.ShouldContainKey(Owner);
        roles[Owner].ShouldBe(new[] { "owner", "reviewer" });
    }

    [Fact]
    public async Task GetAgentMemberRoles_BothSources_UnionsAndDedupesCaseInsensitively()
    {
        // Membership roles first in order, then the definition role appended
        // only if not already present (case-insensitive).
        var fixture = new Fixture()
            .WithAgent(StaffWriter, role: "managing-editor")
            .WithMembership(UnitId, StaffWriter, membershipRoles: new[] { "owner", "Staff-Writer" })
            .WithAgent(FactChecker, role: "reviewer")
            .WithMembership(UnitId, FactChecker, membershipRoles: new[] { "reviewer" })
            .Build();

        var roles = await fixture.GetAgentMemberRolesAsync(UnitId);

        roles[StaffWriter].ShouldBe(new[] { "owner", "Staff-Writer", "managing-editor" });
        // FactChecker's definition role equals its single membership role
        // (case-insensitive) — the append must dedupe it away.
        roles[FactChecker].ShouldBe(new[] { "reviewer" });
    }

    [Fact]
    public async Task GetAgentMemberRoles_AgentWithNoRoles_IsOmitted()
    {
        // An agent with neither a membership role nor a definition role is
        // omitted from the map so callers can treat "absent" as "no roles".
        var fixture = new Fixture()
            .WithAgent(StaffWriter, role: null)
            .WithMembership(UnitId, StaffWriter, membershipRoles: Array.Empty<string>())
            .Build();

        var roles = await fixture.GetAgentMemberRolesAsync(UnitId);

        roles.ShouldNotContainKey(StaffWriter);
        roles.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAgentMemberRoles_OnlyMembersOfTheRequestedUnit()
    {
        // A membership in a different unit must not leak into the requested
        // unit's result — the join is scoped to unit_memberships.unit_id.
        var fixture = new Fixture()
            .WithAgent(StaffWriter, role: "staff-writer")
            .WithMembership(UnitId, StaffWriter, membershipRoles: Array.Empty<string>())
            .WithAgent(Owner, role: "owner")
            .WithMembership(OtherUnitId, Owner, membershipRoles: Array.Empty<string>())
            .Build();

        var roles = await fixture.GetAgentMemberRolesAsync(UnitId);

        roles.Keys.ShouldBe(new[] { StaffWriter });
    }

    [Fact]
    public async Task GetAgentMemberRoles_MembershipWithoutAgentDefinition_FallsBackToMembershipRoles()
    {
        // The left join keeps a membership whose agent-definition row is
        // missing; its effective roles are the membership roles alone.
        var fixture = new Fixture()
            .WithMembership(UnitId, Owner, membershipRoles: new[] { "owner" })
            .Build();

        var roles = await fixture.GetAgentMemberRolesAsync(UnitId);

        roles[Owner].ShouldBe(new[] { "owner" });
    }

    private sealed class Fixture
    {
        private readonly List<AgentDefinitionEntity> _agents = new();
        private readonly List<UnitMembershipEntity> _memberships = new();
        private DateTimeOffset _nextCreatedAt = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public Fixture WithAgent(Guid agentId, string? role)
        {
            _agents.Add(new AgentDefinitionEntity
            {
                Id = agentId,
                TenantId = TenantId,
                DisplayName = $"agent-{agentId:N}",
                Description = "test agent",
                Role = role,
            });
            return this;
        }

        public Fixture WithMembership(Guid unitId, Guid agentId, IReadOnlyList<string> membershipRoles)
        {
            _memberships.Add(new UnitMembershipEntity
            {
                TenantId = TenantId,
                UnitId = unitId,
                AgentId = agentId,
                Roles = membershipRoles.ToList(),
                Expertise = new List<string>(),
                CreatedAt = _nextCreatedAt,
                UpdatedAt = _nextCreatedAt,
            });
            _nextCreatedAt = _nextCreatedAt.AddMinutes(1);
            return this;
        }

        public BuiltFixture Build()
        {
            var services = new ServiceCollection();
            services.AddSingleton<ITenantContext>(new StaticTenantContext(TenantId));

            var dbName = "sv-member-role-directory-" + Guid.NewGuid().ToString("N");
            services.AddDbContext<SpringDbContext>(opt => opt
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

            var sp = services.BuildServiceProvider();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
                db.AgentDefinitions.AddRange(_agents);
                db.UnitMemberships.AddRange(_memberships);
                db.SaveChanges();
            }

            return new BuiltFixture(new UnitMemberRoleDirectory(scopeFactory));
        }
    }

    private sealed class BuiltFixture(UnitMemberRoleDirectory directory)
    {
        public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> GetAgentMemberRolesAsync(Guid unitId) =>
            await directory.GetAgentMemberRolesAsync(unitId, TestContext.Current.CancellationToken);
    }
}
