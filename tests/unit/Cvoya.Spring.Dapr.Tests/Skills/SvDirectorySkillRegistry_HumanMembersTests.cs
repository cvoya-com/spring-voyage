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
/// Tests for the ADR-0044 § 5 / ADR-0046 §9 extension:
/// <c>sv.directory.list_members</c> folds package-declared human team members into
/// the homogeneous response, gated by <c>kind == "human"</c>. ADR-0046 §9
/// replaces the per-row <c>team_role: string</c> field with a multi-valued
/// <c>roles: string[]</c> array.
/// </summary>
public class SvDirectorySkillRegistry_HumanMembersTests
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-1111-1111-1111-000000000001");
    private static readonly Guid UnitId = Guid.Parse("bbbbbbbb-2222-2222-2222-000000000001");
    private static readonly Guid CallerId = Guid.Parse("cccccccc-3333-3333-3333-000000000001");

    [Fact]
    public async Task ListMembers_UnitWithHumans_IncludesHumanEntries()
    {
        var aliceId = Guid.Parse("00000000-aaaa-aaaa-aaaa-000000000001");
        var sut = new Fixture()
            .WithHumanDisplayName(aliceId, "Alice")
            .SeedHuman(aliceId, roles: new[] { "owner" }, expertise: new[] { "security" })
            .Build();

        var entries = await sut.ListMembersAsync();

        var humans = entries.Where(e => e.Kind == SvDirectorySkillRegistry.KindHuman).ToList();
        humans.Count.ShouldBe(1);
        var entry = humans[0];
        entry.Uuid.ShouldBe(GuidFormatter.Format(aliceId));
        entry.DisplayName.ShouldBe("Alice");
        entry.Roles.ShouldBe(new[] { "owner" });
        entry.Expertise.Select(e => e.Name).ShouldBe(new[] { "security" });
        entry.ParentUuids.ShouldHaveSingleItem().ShouldBe(GuidFormatter.Format(UnitId));
        // member_count is not populated for humans (humans aren't aggregates).
        entry.MemberCount.ShouldBeNull();
        // #2491: humans carry no live_status — the field is omitted from
        // the wire shape entirely (absence, not null).
        entry.HasLiveStatus.ShouldBeFalse();
    }

    [Fact]
    public async Task ListMembers_HumanWithMultipleRoles_EmitsOneEntryWithRolesArray()
    {
        // ADR-0046 §7 + §9: a single human filling multiple team roles
        // surfaces as ONE entry whose roles array carries every role —
        // a behaviour change from ADR-0044's "one entry per (human, role)
        // row".
        var bobId = Guid.Parse("00000000-bbbb-bbbb-bbbb-000000000001");
        var sut = new Fixture()
            .WithHumanDisplayName(bobId, "Bob")
            .SeedHuman(bobId, roles: new[] { "owner", "security_lead" }, expertise: new[] { "infra" })
            .Build();

        var entries = await sut.ListMembersAsync();

        var humans = entries.Where(e => e.Kind == SvDirectorySkillRegistry.KindHuman).ToList();
        humans.Count.ShouldBe(1);
        humans[0].Uuid.ShouldBe(GuidFormatter.Format(bobId));
        humans[0].Roles.ShouldBe(new[] { "owner", "security_lead" });
    }

    [Fact]
    public async Task ListMembers_HumanWithoutRoles_EmitsEmptyRolesArray()
    {
        // ADR-0046 §9: the `roles` field is emitted uniformly on every
        // entry kind — a human with no roles still carries `"roles": []`
        // so clients can treat the field as a stable `string[]` without
        // distinguishing missing from empty.
        var noRolesId = Guid.Parse("00000000-cccc-cccc-cccc-000000000001");
        var sut = new Fixture()
            .WithHumanDisplayName(noRolesId, "RoleLess")
            .SeedHuman(noRolesId, roles: Array.Empty<string>())
            .Build();

        var json = await sut.ListMembersAsJsonAsync();

        var members = json.GetProperty("members");
        foreach (var entry in members.EnumerateArray())
        {
            if (string.Equals(entry.GetProperty("kind").GetString(),
                SvDirectorySkillRegistry.KindHuman, StringComparison.Ordinal))
            {
                entry.TryGetProperty("roles", out var rolesEl).ShouldBeTrue();
                rolesEl.ValueKind.ShouldBe(JsonValueKind.Array);
                rolesEl.GetArrayLength().ShouldBe(0);
            }
        }
    }

    // ── ADR-0046 §8: agent entries surface per-membership roles ──────────

    [Fact]
    public async Task ListMembers_AgentWithMembershipRoles_EmitsRolesArray()
    {
        // ADR-0046 §8: agent entries surface the per-membership roles list
        // (the same multi-valued shape the human entries carry, additive on
        // agent rows). The supplement runs after BuildEntryAsync, so the
        // test seeds an agent definition in EF plus a unit-membership row
        // and asserts the resulting JSON entry carries roles[] verbatim.
        var agentId = Guid.Parse("00000000-dddd-dddd-dddd-000000000001");
        var sut = new Fixture()
            .SeedAgentMembership(agentId,
                roles: new[] { "tech-lead", "ic" },
                expertise: new[] { "kubernetes" })
            .Build();

        var json = await sut.ListMembersAsJsonAsync();

        var members = json.GetProperty("members");
        JsonElement? agentEntry = null;
        foreach (var entry in members.EnumerateArray())
        {
            if (string.Equals(entry.GetProperty("kind").GetString(),
                Address.AgentScheme, StringComparison.Ordinal))
            {
                agentEntry = entry;
                break;
            }
        }

        agentEntry.ShouldNotBeNull();
        agentEntry!.Value.TryGetProperty("roles", out var rolesEl).ShouldBeTrue();
        rolesEl.ValueKind.ShouldBe(JsonValueKind.Array);
        var roleValues = rolesEl.EnumerateArray()
            .Select(r => r.GetString() ?? string.Empty)
            .ToList();
        roleValues.ShouldBe(new[] { "tech-lead", "ic" });
    }

    [Fact]
    public async Task ListMembers_AgentWithEmptyMembershipRoles_EmitsEmptyRolesArray()
    {
        // Mirrors ListMembers_HumanWithoutRoles_EmitsEmptyRolesArray for
        // agent entries — when the per-membership roles list is empty,
        // the agent entry still carries `"roles": []` so the wire shape
        // stays uniform across entry kinds per ADR-0046 §9.
        var agentId = Guid.Parse("00000000-dddd-dddd-dddd-000000000002");
        var sut = new Fixture()
            .SeedAgentMembership(agentId,
                roles: Array.Empty<string>(),
                expertise: Array.Empty<string>())
            .Build();

        var json = await sut.ListMembersAsJsonAsync();

        var members = json.GetProperty("members");
        foreach (var entry in members.EnumerateArray())
        {
            if (string.Equals(entry.GetProperty("kind").GetString(),
                Address.AgentScheme, StringComparison.Ordinal))
            {
                entry.TryGetProperty("roles", out var rolesEl).ShouldBeTrue();
                rolesEl.ValueKind.ShouldBe(JsonValueKind.Array);
                rolesEl.GetArrayLength().ShouldBe(0);
            }
        }
    }

    // ── fixture ──────────────────────────────────────────────────────────

    private sealed class Fixture
    {
        private readonly InMemoryUnitHumanMembershipStore _membershipStore = new();
        private readonly Dictionary<Guid, string> _humanDisplayNames = new();
        private readonly List<UnitMembership> _agentMemberships = new();
        private readonly List<Guid> _seededAgents = new();

        public Fixture WithHumanDisplayName(Guid humanId, string displayName)
        {
            _humanDisplayNames[humanId] = displayName;
            return this;
        }

        public Fixture SeedHuman(
            Guid humanId,
            IReadOnlyList<string>? roles = null,
            IReadOnlyList<string>? expertise = null,
            IReadOnlyList<string>? notifications = null)
        {
            _membershipStore.Seed(UnitId, humanId,
                roles ?? Array.Empty<string>(),
                expertise ?? Array.Empty<string>(),
                notifications ?? Array.Empty<string>());
            return this;
        }

        /// <summary>
        /// Seeds an agent member of <see cref="UnitId"/> with a per-membership
        /// roles + expertise list. Adds the agent to the member graph (so it
        /// surfaces under sv.directory.list_members) plus a UnitMembership row that the
        /// SvDirectorySkillRegistry reads via IUnitMembershipRepository to
        /// supplement the entry.
        /// </summary>
        public Fixture SeedAgentMembership(
            Guid agentId,
            IReadOnlyList<string>? roles = null,
            IReadOnlyList<string>? expertise = null)
        {
            _seededAgents.Add(agentId);
            _agentMemberships.Add(new UnitMembership(
                UnitId: UnitId,
                AgentId: agentId,
                Roles: roles,
                Expertise: expertise));
            return this;
        }

        public BuiltFixture Build()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            // In-memory SpringDbContext so the registry's ReadDefinitionAsync
            // call against AgentDefinitions resolves without a real DB.
            //
            // #2498: capture the database name OUTSIDE the UseInMemoryDatabase
            // delegate. Without this, every DbContext resolution within a
            // single test re-evaluates the delegate and gets a new Guid —
            // each scope sees an empty database, so seeded rows are
            // invisible to the registry's read path.
            var dbName = "sv-directory-tests-" + Guid.NewGuid().ToString("N");
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
            // The directory-read enforcer is wired by Core in production via
            // DI; tests substitute a permissive one so list_members reaches
            // the human-folding branch.
            var enforcer = Substitute.For<IUnitPolicyEnforcer>();
            enforcer
                .EvaluateUnitDirectoryReadAsync(
                    Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(PolicyDecision.Allowed);
            services.AddScoped<IUnitPolicyEnforcer>(_ => enforcer);
            // IParticipantDisplayNameResolver is referenced by
            // BuildEntryAsync's non-human path; humans take a different
            // path. Wire a substitute so the agent / unit entries don't
            // throw if the test ever expands.
            var participantResolver = Substitute.For<IParticipantDisplayNameResolver>();
            participantResolver
                .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => new ValueTask<string>(call.ArgAt<string>(0)));
            services.AddScoped<IParticipantDisplayNameResolver>(_ => participantResolver);

            // ADR-0046 §8: BuildHumanEntryAsync no longer reads the agent /
            // unit-membership repo when emitting human entries, but the
            // sibling agent / unit folding path does. Wire a fake repo that
            // returns the seeded agent-membership rows so the test exercising
            // agent-roles emission has somewhere to read them from.
            var membershipRepo = Substitute.For<IUnitMembershipRepository>();
            membershipRepo
                .ListByUnitAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(_agentMemberships);
            membershipRepo
                .ListByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var agentId = call.ArgAt<Guid>(0);
                    return _agentMemberships
                        .Where(m => m.AgentId == agentId)
                        .ToList();
                });
            services.AddScoped<IUnitMembershipRepository>(_ => membershipRepo);

            var sp = services.BuildServiceProvider();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

            // Seed AgentDefinitions for each agent the test wired into the
            // member graph so ReadDefinitionAsync returns a description and
            // BuildEntryAsync's downstream code path runs to completion.
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

            var memberGraph = new InMemoryUnitMemberGraphStore();
            foreach (var agentId in _seededAgents)
            {
                memberGraph.SeedAgentMembers(UnitId, agentId);
            }
            var expertiseStore = Substitute.For<IExpertiseStore>();
            expertiseStore
                .GetDomainsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<ExpertiseDomain>());

            var tenantContext = Substitute.For<ITenantContext>();
            tenantContext.CurrentTenantId.Returns(TenantId);

            var loggerFactory = NullLoggerFactory.Instance;

            // #2491: the registry calls IActorProxyFactory to resolve
            // live_status for agent / unit entries. For the human-members
            // tests we don't exercise the live-status path (the assertions
            // pin the human / role projection only), so a substitute that
            // returns null proxies is enough — the actor calls then
            // throw and the registry tolerates the failure by omitting
            // live_status from the affected entries.
            var actorProxyFactory = Substitute.For<IActorProxyFactory>();

            var registry = new SvDirectorySkillRegistry(
                scopeFactory,
                memberGraph,
                _membershipStore,
                expertiseStore,
                actorProxyFactory,
                tenantContext,
                loggerFactory);

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

        public async Task<IReadOnlyList<EntryProjection>> ListMembersAsync()
        {
            var json = await ListMembersAsJsonAsync();
            var members = json.GetProperty("members");
            var result = new List<EntryProjection>();
            foreach (var entry in members.EnumerateArray())
            {
                result.Add(EntryProjection.From(entry));
            }
            return result;
        }

        public async Task<JsonElement> ListMembersAsJsonAsync()
        {
            var args = JsonDocument.Parse($$"""{ "uuid": "{{GuidFormatter.Format(UnitId)}}" }""").RootElement;
            var ctx = new ToolCallContext(
                CallerId: GuidFormatter.Format(CallerId),
                CallerKind: SvDirectorySkillRegistry.KindHuman,
                ThreadId: Guid.NewGuid().ToString("N"));

            return await _registry.InvokeAsync(
                SvDirectorySkillRegistry.ListMembersTool,
                args,
                ctx,
                TestContext.Current.CancellationToken);
        }
    }

    private sealed record EntryProjection(
        string Uuid,
        string Kind,
        string DisplayName,
        IReadOnlyList<string> ParentUuids,
        IReadOnlyList<ExpertiseProjection> Expertise,
        int? MemberCount,
        bool HasLiveStatus,
        IReadOnlyList<string> Roles)
    {
        public static EntryProjection From(JsonElement el)
        {
            var parentUuids = el.GetProperty("parent_uuids").EnumerateArray()
                .Select(e => e.GetString() ?? string.Empty).ToList();
            var expertise = el.GetProperty("expertise").EnumerateArray()
                .Select(e => new ExpertiseProjection(
                    Name: e.GetProperty("name").GetString() ?? string.Empty))
                .ToList();
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
            int? memberCount = null;
            if (el.GetProperty("member_count").ValueKind == JsonValueKind.Number)
            {
                memberCount = el.GetProperty("member_count").GetInt32();
            }
            return new EntryProjection(
                Uuid: el.GetProperty("uuid").GetString() ?? string.Empty,
                Kind: el.GetProperty("kind").GetString() ?? string.Empty,
                DisplayName: el.GetProperty("display_name").GetString() ?? string.Empty,
                ParentUuids: parentUuids,
                Expertise: expertise,
                MemberCount: memberCount,
                HasLiveStatus: el.TryGetProperty("live_status", out _),
                Roles: roles);
        }
    }

    private sealed record ExpertiseProjection(string Name);
}
