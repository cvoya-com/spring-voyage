// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Skills;
using Cvoya.Spring.Dapr.Tests.TestHelpers;

using global::Dapr.Actors.Client;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <c>sv.directory.members</c> (#3065) — the caller-facing roster of
/// its own unit membership (agents, sub-units, and human members) projected to
/// sendable addresses, so a member can resolve a teammate (including a human
/// such as the publisher / approver) to an address without routing through the
/// hub.
/// </summary>
public class SvDirectorySkillRegistry_MembersTests
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-9999-9999-9999-000000000001");
    private static readonly Guid UnitId = Guid.Parse("bbbbbbbb-9999-9999-9999-000000000001");
    private static readonly Guid CallerAgentId = Guid.Parse("cccccccc-9999-9999-9999-000000000001");

    [Fact]
    public async Task Members_AgentCaller_ReturnsParentUnitMembersWithAddresses()
    {
        var teammateAgentId = Guid.Parse("00000000-aaaa-9999-9999-000000000001");
        var subUnitId = Guid.Parse("00000000-bbbb-9999-9999-000000000001");
        var sut = new Fixture()
            .WithAgentTeammate(teammateAgentId)
            .WithSubUnitMember(subUnitId)
            .Build();

        var roster = await sut.MembersAsync();

        var addresses = roster.Select(e => e.Address).ToList();
        addresses.ShouldContain(new Address(Address.AgentScheme, teammateAgentId).ToString());
        addresses.ShouldContain(new Address(Address.UnitScheme, subUnitId).ToString());

        var agentEntry = roster.Single(e => e.Kind == Address.AgentScheme);
        // role_or_kind falls back to the kind when the member has no role.
        agentEntry.RoleOrKind.ShouldBe(Address.AgentScheme);

        var unitEntry = roster.Single(e => e.Kind == Address.UnitScheme);
        unitEntry.RoleOrKind.ShouldBe(Address.UnitScheme);
    }

    [Fact]
    public async Task Members_AgentCaller_IncludesHumanMembersAddressableByRole()
    {
        // The load-bearing scenario from #3065: a member needs to address a
        // human teammate (the publisher, role 'approver') on its own.
        var publisherId = Guid.Parse("00000000-cccc-9999-9999-000000000001");
        var sut = new Fixture()
            .WithHumanMember(publisherId, "Pat Publisher", roles: new[] { "approver" })
            .Build();

        var roster = await sut.MembersAsync();

        var human = roster.Single(e => e.Kind == SvDirectorySkillRegistry.KindHuman);
        human.Address.ShouldBe(new Address(Address.HumanScheme, publisherId).ToString());
        human.DisplayName.ShouldBe("Pat Publisher");
        human.Roles.ShouldBe(new[] { "approver" });
        // A human with a role surfaces that role as role_or_kind so the caller
        // can pick out "the approver" without a second lookup.
        human.RoleOrKind.ShouldBe("approver");
    }

    [Fact]
    public async Task Members_HumanWithoutRoles_FallsBackToHumanKind()
    {
        var humanId = Guid.Parse("00000000-dddd-9999-9999-000000000001");
        var sut = new Fixture()
            .WithHumanMember(humanId, "Roleless Human", roles: Array.Empty<string>())
            .Build();

        var roster = await sut.MembersAsync();

        var human = roster.Single(e => e.Kind == SvDirectorySkillRegistry.KindHuman);
        human.Roles.ShouldBeEmpty();
        human.RoleOrKind.ShouldBe(SvDirectorySkillRegistry.KindHuman);
    }

    [Fact]
    public async Task Members_EmptyUnit_ReturnsEmptyRosterWithZeroTotal()
    {
        var sut = new Fixture().Build();

        var json = await sut.MembersAsJsonAsync();

        json.GetProperty("members").GetArrayLength().ShouldBe(0);
        json.GetProperty("total_count").GetInt32().ShouldBe(0);
    }

    // ── fixture ──────────────────────────────────────────────────────────

    private sealed class Fixture
    {
        private readonly InMemoryUnitHumanMembershipStore _humanStore = new();
        private readonly InMemoryUnitMemberGraphStore _memberGraph = new();
        private readonly Dictionary<Guid, string> _humanDisplayNames = new();
        private readonly List<Guid> _seededAgents = new();

        public Fixture WithAgentTeammate(Guid agentId)
        {
            _seededAgents.Add(agentId);
            _memberGraph.SeedAgentMembers(UnitId, agentId);
            return this;
        }

        public Fixture WithSubUnitMember(Guid subUnitId)
        {
            _memberGraph.SeedSubunitChildren(UnitId, subUnitId);
            return this;
        }

        public Fixture WithHumanMember(Guid humanId, string displayName, IReadOnlyList<string> roles)
        {
            _humanDisplayNames[humanId] = displayName;
            _humanStore.Seed(UnitId, humanId, roles: roles);
            return this;
        }

        public BuiltFixture Build()
        {
            var services = new ServiceCollection();
            services.AddLogging();

            var dbName = "sv-directory-members-tests-" + Guid.NewGuid().ToString("N");
            services.AddDbContext<SpringDbContext>(opt => opt
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

            var identityResolver = Substitute.For<IHumanIdentityResolver>();
            foreach (var (id, name) in _humanDisplayNames)
            {
                identityResolver
                    .GetDisplayNameAsync(id, Arg.Any<CancellationToken>())
                    .Returns(name);
            }
            services.AddScoped<IHumanIdentityResolver>(_ => identityResolver);

            // Permissive directory-read enforcer so the roster reaches the
            // member-folding path (the authz denial path is covered elsewhere).
            var enforcer = Substitute.For<IUnitPolicyEnforcer>();
            enforcer
                .EvaluateUnitDirectoryReadAsync(
                    Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(PolicyDecision.Allowed);
            services.AddScoped<IUnitPolicyEnforcer>(_ => enforcer);

            // Non-human members resolve their display name through the
            // participant resolver (echo the address back).
            var participantResolver = Substitute.For<IParticipantDisplayNameResolver>();
            participantResolver
                .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => new ValueTask<string>(call.ArgAt<string>(0)));
            services.AddScoped<IParticipantDisplayNameResolver>(_ => participantResolver);

            // The agent caller resolves its parent unit through the membership
            // repo (ListByAgentAsync → the unit the caller belongs to).
            var membershipRepo = Substitute.For<IUnitMembershipRepository>();
            membershipRepo
                .ListByAgentAsync(CallerAgentId, Arg.Any<CancellationToken>())
                .Returns(new List<UnitMembership>
                {
                    new(UnitId: UnitId, AgentId: CallerAgentId),
                });
            services.AddScoped<IUnitMembershipRepository>(_ => membershipRepo);

            var sp = services.BuildServiceProvider();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

            using (var scope = scopeFactory.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
                foreach (var agentId in _seededAgents)
                {
                    db.AgentDefinitions.Add(new AgentDefinitionEntity
                    {
                        Id = agentId,
                        TenantId = TenantId,
                        DisplayName = $"agent-{agentId:N}",
                        Description = "test agent",
                    });
                }
                db.SaveChanges();
            }

            var expertiseStore = Substitute.For<IExpertiseStore>();
            expertiseStore
                .GetDomainsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<ExpertiseDomain>());

            var tenantContext = Substitute.For<ITenantContext>();
            tenantContext.CurrentTenantId.Returns(TenantId);

            var actorProxyFactory = Substitute.For<IActorProxyFactory>();

            var registry = new SvDirectorySkillRegistry(
                scopeFactory,
                _memberGraph,
                _humanStore,
                expertiseStore,
                actorProxyFactory,
                tenantContext,
                NullLoggerFactory.Instance);

            return new BuiltFixture(registry);
        }
    }

    private sealed class BuiltFixture
    {
        private readonly SvDirectorySkillRegistry _registry;

        public BuiltFixture(SvDirectorySkillRegistry registry)
        {
            _registry = registry;
        }

        public async Task<IReadOnlyList<RosterEntry>> MembersAsync()
        {
            var json = await MembersAsJsonAsync();
            return json.GetProperty("members").EnumerateArray()
                .Select(RosterEntry.From)
                .ToList();
        }

        public async Task<JsonElement> MembersAsJsonAsync()
        {
            var args = JsonDocument.Parse("{}").RootElement;
            var ctx = new ToolCallContext(
                CallerId: GuidFormatter.Format(CallerAgentId),
                CallerKind: Address.AgentScheme,
                ThreadId: Guid.NewGuid().ToString("N"));

            return await _registry.InvokeAsync(
                SvDirectorySkillRegistry.MembersTool,
                args,
                ctx,
                TestContext.Current.CancellationToken);
        }
    }

    private sealed record RosterEntry(
        string Address,
        string DisplayName,
        string RoleOrKind,
        string Kind,
        IReadOnlyList<string> Roles)
    {
        public static RosterEntry From(JsonElement el)
        {
            var roles = new List<string>();
            if (el.TryGetProperty("roles", out var rs) && rs.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in rs.EnumerateArray())
                {
                    if (r.ValueKind == JsonValueKind.String)
                    {
                        roles.Add(r.GetString() ?? string.Empty);
                    }
                }
            }
            return new RosterEntry(
                Address: el.GetProperty("address").GetString() ?? string.Empty,
                DisplayName: el.GetProperty("display_name").GetString() ?? string.Empty,
                RoleOrKind: el.GetProperty("role_or_kind").GetString() ?? string.Empty,
                Kind: el.GetProperty("kind").GetString() ?? string.Empty,
                Roles: roles);
        }
    }
}
