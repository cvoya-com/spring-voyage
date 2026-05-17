// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Skills;
using Cvoya.Spring.Dapr.Tests.TestHelpers;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for the ADR-0044 § 5 extension: <c>sv.list_members</c> folds
/// package-declared human team members into the homogeneous response,
/// gated by <c>kind == "human"</c> and carrying an additional
/// <c>team_role</c> field.
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
            .SeedHuman(aliceId, role: "owner", expertise: new[] { "security" })
            .Build();

        var entries = await sut.ListMembersAsync();

        var humans = entries.Where(e => e.Kind == SvDirectorySkillRegistry.KindHuman).ToList();
        humans.Count.ShouldBe(1);
        var entry = humans[0];
        entry.Uuid.ShouldBe(GuidFormatter.Format(aliceId));
        entry.DisplayName.ShouldBe("Alice");
        entry.TeamRole.ShouldBe("owner");
        entry.Expertise.Select(e => e.Name).ShouldBe(new[] { "security" });
        entry.ParentUuids.ShouldHaveSingleItem().ShouldBe(GuidFormatter.Format(UnitId));
        // member_count is not populated for humans (humans aren't aggregates).
        entry.MemberCount.ShouldBeNull();
        entry.LiveStatus.ShouldBe("n/a");
    }

    [Fact]
    public async Task ListMembers_HumanWithMultipleRoles_EmitsOneEntryPerRole()
    {
        // ADR-0044 § 3 + § 5: a single human filling multiple team roles
        // surfaces as one entry per (human, role) row, with the same uuid
        // and different team_role.
        var bobId = Guid.Parse("00000000-bbbb-bbbb-bbbb-000000000001");
        var sut = new Fixture()
            .WithHumanDisplayName(bobId, "Bob")
            .SeedHuman(bobId, role: "owner")
            .SeedHuman(bobId, role: "security_lead", expertise: new[] { "infra" })
            .Build();

        var entries = await sut.ListMembersAsync();

        var humans = entries.Where(e => e.Kind == SvDirectorySkillRegistry.KindHuman).ToList();
        humans.Count.ShouldBe(2);
        humans.All(e => e.Uuid == GuidFormatter.Format(bobId)).ShouldBeTrue();
        humans.Select(e => e.TeamRole).ShouldBe(new[] { "owner", "security_lead" }, ignoreOrder: true);
    }

    [Fact]
    public async Task ListMembers_TeamRoleOnlyGatedOnHumanKind()
    {
        // Regression guard for the gating rule in WriteEntry: agent / unit
        // entries never emit a team_role field. The byte-for-byte
        // compatibility claim in the ADR depends on this.
        var sut = new Fixture()
            .SeedHuman(Guid.NewGuid(), role: "owner")
            .Build();

        var json = await sut.ListMembersAsJsonAsync();

        // Every non-human entry must NOT have a team_role property.
        var members = json.GetProperty("members");
        foreach (var entry in members.EnumerateArray())
        {
            if (!string.Equals(entry.GetProperty("kind").GetString(),
                SvDirectorySkillRegistry.KindHuman, StringComparison.Ordinal))
            {
                entry.TryGetProperty("team_role", out _).ShouldBeFalse();
            }
        }
    }

    // ── fixture ──────────────────────────────────────────────────────────

    private sealed class Fixture
    {
        private readonly InMemoryUnitHumanMembershipStore _membershipStore = new();
        private readonly Dictionary<Guid, string> _humanDisplayNames = new();

        public Fixture WithHumanDisplayName(Guid humanId, string displayName)
        {
            _humanDisplayNames[humanId] = displayName;
            return this;
        }

        public Fixture SeedHuman(
            Guid humanId,
            string role,
            IReadOnlyList<string>? expertise = null,
            IReadOnlyList<string>? notifications = null)
        {
            _membershipStore.Seed(UnitId, humanId, role,
                expertise ?? Array.Empty<string>(),
                notifications ?? Array.Empty<string>());
            return this;
        }

        public BuiltFixture Build()
        {
            var services = new ServiceCollection();
            services.AddLogging();
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

            var sp = services.BuildServiceProvider();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

            var memberGraph = new InMemoryUnitMemberGraphStore();
            var expertiseStore = Substitute.For<IExpertiseStore>();
            expertiseStore
                .GetDomainsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<ExpertiseDomain>());

            var tenantContext = Substitute.For<ITenantContext>();
            tenantContext.CurrentTenantId.Returns(TenantId);

            var loggerFactory = NullLoggerFactory.Instance;

            var registry = new SvDirectorySkillRegistry(
                scopeFactory,
                memberGraph,
                _membershipStore,
                expertiseStore,
                BuildPersistentAgentRegistry(loggerFactory),
                tenantContext,
                loggerFactory);

            return new BuiltFixture(registry);
        }

        private static PersistentAgentRegistry BuildPersistentAgentRegistry(ILoggerFactory loggerFactory)
        {
            var containerRuntime = Substitute.For<IContainerRuntime>();
            var lifecycle = new ContainerLifecycleManager(
                containerRuntime,
                Substitute.For<IDaprSidecarManager>(),
                Options.Create(new DaprSidecarOptions()),
                loggerFactory);
            var volumes = new AgentVolumeManager(containerRuntime, loggerFactory);
            return new PersistentAgentRegistry(
                containerRuntime,
                Substitute.For<IHttpClientFactory>(),
                lifecycle,
                volumes,
                Substitute.For<IServiceScopeFactory>(),
                loggerFactory);
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
        string LiveStatus,
        string? TeamRole)
    {
        public static EntryProjection From(JsonElement el)
        {
            var parentUuids = el.GetProperty("parent_uuids").EnumerateArray()
                .Select(e => e.GetString() ?? string.Empty).ToList();
            var expertise = el.GetProperty("expertise").EnumerateArray()
                .Select(e => new ExpertiseProjection(
                    Name: e.GetProperty("name").GetString() ?? string.Empty))
                .ToList();
            string? teamRole = null;
            if (el.TryGetProperty("team_role", out var tr) && tr.ValueKind == JsonValueKind.String)
            {
                teamRole = tr.GetString();
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
                LiveStatus: el.GetProperty("live_status").GetString() ?? string.Empty,
                TeamRole: teamRole);
        }
    }

    private sealed record ExpertiseProjection(string Name);
}
