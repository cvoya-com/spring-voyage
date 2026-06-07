// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Skills;
using Cvoya.Spring.Dapr.Tests.TestHelpers;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for the #2491 extensions to <see cref="SvDirectorySkillRegistry"/>:
/// <c>live_status</c> on directory entries (rich object replacing the prior
/// string) and the new <c>sv.directory.get_status</c> tool. Pin the four acceptance
/// scenarios from the issue: busy agent visible via MCP, busy unit visible
/// via MCP, human entries lack <c>live_status</c>, unknown uuid on
/// <c>sv.directory.get_status</c> returns the expected typed error.
/// </summary>
public class SvDirectorySkillRegistry_LiveStatusTests
{
    private static readonly Guid TenantId = Guid.Parse("aaaaaaaa-1111-1111-1111-000000000001");
    private static readonly Guid UnitId = Guid.Parse("bbbbbbbb-2222-2222-2222-000000000001");
    private static readonly Guid CallerId = Guid.Parse("cccccccc-3333-3333-3333-000000000001");

    private static readonly Guid BusyAgentId = Guid.Parse("00000000-aaaa-aaaa-aaaa-000000000001");
    private static readonly Guid BusyUnitId = Guid.Parse("00000000-bbbb-bbbb-bbbb-000000000002");
    private static readonly Guid HumanId = Guid.Parse("00000000-cccc-cccc-cccc-000000000003");

    [Fact]
    public async Task ListMembers_BusyAgent_ExposesLiveStatusReport()
    {
        // Acceptance (a): a busy agent surfaced via sv.directory.list_members carries
        // the rich live_status object — in_flight / queued / channels /
        // observed_at — populated from the actor's GetRuntimeStatusAsync.
        var agentReport = new AgentRuntimeStatusReport(
            InFlightThreadCount: 2,
            QueuedMessageCount: 3,
            ChannelCount: 2,
            ObservedAt: DateTimeOffset.UtcNow);

        var fixture = new Fixture()
            .SeedAgent(BusyAgentId, runtimeStatus: agentReport)
            .Build();

        var json = await fixture.ListMembersAsJsonAsync();

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
        agentEntry!.Value.TryGetProperty("live_status", out var liveStatus).ShouldBeTrue();
        liveStatus.ValueKind.ShouldBe(JsonValueKind.Object);
        liveStatus.GetProperty("in_flight").GetInt32().ShouldBe(2);
        liveStatus.GetProperty("queued").GetInt32().ShouldBe(3);
        liveStatus.GetProperty("channels").GetInt32().ShouldBe(2);
        liveStatus.TryGetProperty("observed_at", out var observedAt).ShouldBeTrue();
        observedAt.ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task ListMembers_BusyUnit_ExposesLiveStatusReport()
    {
        // Acceptance (b): a busy unit member surfaces non-zero in-flight
        // via the same live_status object shape as agents. The unit's
        // GetRuntimeStatusAsync is now populated from its
        // ActorDispatchChannelTracker (#2491) — the test wires the unit
        // proxy to the substitute report so the registry's read path is
        // independent of the actor internals.
        var unitReport = new AgentRuntimeStatusReport(
            InFlightThreadCount: 1,
            QueuedMessageCount: 0,
            ChannelCount: 1,
            ObservedAt: DateTimeOffset.UtcNow);

        var fixture = new Fixture()
            .SeedSubUnit(BusyUnitId, runtimeStatus: unitReport)
            .Build();

        var json = await fixture.ListMembersAsJsonAsync();

        var members = json.GetProperty("members");
        JsonElement? unitEntry = null;
        foreach (var entry in members.EnumerateArray())
        {
            if (string.Equals(entry.GetProperty("kind").GetString(),
                Address.UnitScheme, StringComparison.Ordinal)
                && string.Equals(entry.GetProperty("uuid").GetString(),
                    GuidFormatter.Format(BusyUnitId), StringComparison.Ordinal))
            {
                unitEntry = entry;
                break;
            }
        }

        unitEntry.ShouldNotBeNull();
        unitEntry!.Value.TryGetProperty("live_status", out var liveStatus).ShouldBeTrue();
        liveStatus.GetProperty("in_flight").GetInt32().ShouldBe(1);
        liveStatus.GetProperty("queued").GetInt32().ShouldBe(0);
        liveStatus.GetProperty("channels").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task ListMembers_HumanEntry_OmitsLiveStatusKeyEntirely()
    {
        // Acceptance (c): humans have NO live_status in v0.1. The
        // serializer must omit the field — the field's absence is the
        // contract, not a null value. Mirrors the issue body's
        // "Omit it entirely for kind: human" rule.
        var fixture = new Fixture()
            .SeedHumanMember(HumanId, displayName: "Alice")
            .Build();

        var json = await fixture.ListMembersAsJsonAsync();

        var members = json.GetProperty("members");
        var humanFound = false;
        foreach (var entry in members.EnumerateArray())
        {
            if (string.Equals(entry.GetProperty("kind").GetString(),
                SvDirectorySkillRegistry.KindHuman, StringComparison.Ordinal))
            {
                humanFound = true;
                entry.TryGetProperty("live_status", out _).ShouldBeFalse(
                    "human entries MUST omit live_status from the wire shape entirely");
            }
        }
        humanFound.ShouldBeTrue();
    }

    [Fact]
    public async Task ListMembers_PerSubjectFailure_OmitsLiveStatusButKeepsOtherEntries()
    {
        // The issue requires: "a broken single-subject status query does
        // not break the whole list response". Wire one agent to throw on
        // GetRuntimeStatusAsync and another to return a clean report; the
        // failing entry simply lacks live_status while the other entry
        // still carries it.
        var workingId = Guid.Parse("00000000-1111-1111-1111-000000000010");
        var failingId = Guid.Parse("00000000-1111-1111-1111-000000000011");

        var workingReport = new AgentRuntimeStatusReport(
            InFlightThreadCount: 1, QueuedMessageCount: 0,
            ChannelCount: 1, ObservedAt: DateTimeOffset.UtcNow);

        var fixture = new Fixture()
            .SeedAgent(workingId, runtimeStatus: workingReport)
            .SeedAgent(failingId, runtimeStatusError: new InvalidOperationException("simulated failure"))
            .Build();

        var json = await fixture.ListMembersAsJsonAsync();

        var workingEntry = FindEntry(json.GetProperty("members"), workingId, Address.AgentScheme);
        var failingEntry = FindEntry(json.GetProperty("members"), failingId, Address.AgentScheme);

        workingEntry.ShouldNotBeNull();
        failingEntry.ShouldNotBeNull();

        workingEntry!.Value.TryGetProperty("live_status", out _).ShouldBeTrue();
        failingEntry!.Value.TryGetProperty("live_status", out _).ShouldBeFalse(
            "an actor-proxy failure on one entry must not break the whole list response");
    }

    [Fact]
    public async Task GetStatus_BusyAgent_ReturnsSlimProjectionWithLiveStatus()
    {
        // sv.directory.get_status(uuid) returns the same { uuid, kind, display_name,
        // live_status? } shape for one subject. This is the per-subject
        // poll path the issue introduces alongside the list extension.
        var agentReport = new AgentRuntimeStatusReport(
            InFlightThreadCount: 1, QueuedMessageCount: 0,
            ChannelCount: 1, ObservedAt: DateTimeOffset.UtcNow);
        var fixture = new Fixture()
            .SeedAgent(BusyAgentId, runtimeStatus: agentReport)
            .Build();

        var json = await fixture.GetStatusAsJsonAsync(BusyAgentId);

        json.GetProperty("uuid").GetString().ShouldBe(GuidFormatter.Format(BusyAgentId));
        json.GetProperty("kind").GetString().ShouldBe(Address.AgentScheme);
        json.TryGetProperty("display_name", out _).ShouldBeTrue();
        json.TryGetProperty("live_status", out var liveStatus).ShouldBeTrue();
        liveStatus.GetProperty("in_flight").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task GetStatus_UnknownUuid_ThrowsArgumentException()
    {
        // Acceptance (d): align the error shape on sv.directory.get_status with what
        // sv.directory.get_member already does for unknown UUIDs. sv.directory.get_member's
        // ResolveKindAsync throws ArgumentException when no agent / unit /
        // tenant matches; sv.directory.get_status must throw the same way so MCP
        // surfaces a single typed error contract for both tools.
        var unknownId = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        var fixture = new Fixture().Build();

        var ex = await Should.ThrowAsync<ArgumentException>(async () =>
            await fixture.GetStatusAsJsonAsync(unknownId));
        ex.Message.ShouldContain(GuidFormatter.Format(unknownId));
    }

    private static JsonElement? FindEntry(JsonElement members, Guid id, string kind)
    {
        var targetUuid = GuidFormatter.Format(id);
        foreach (var entry in members.EnumerateArray())
        {
            if (string.Equals(entry.GetProperty("kind").GetString(), kind, StringComparison.Ordinal)
                && string.Equals(entry.GetProperty("uuid").GetString(), targetUuid, StringComparison.Ordinal))
            {
                return entry;
            }
        }
        return null;
    }

    // ── fixture ──────────────────────────────────────────────────────────

    private sealed class Fixture
    {
        private readonly InMemoryUnitHumanMembershipStore _membershipStore = new();
        private readonly Dictionary<Guid, AgentRuntimeStatusReport> _agentReports = new();
        private readonly Dictionary<Guid, Exception> _agentErrors = new();
        private readonly Dictionary<Guid, AgentRuntimeStatusReport> _unitReports = new();
        private readonly Dictionary<Guid, Exception> _unitErrors = new();
        private readonly List<Guid> _seededAgents = new();
        private readonly List<Guid> _seededSubUnits = new();
        private readonly Dictionary<Guid, string> _humanDisplayNames = new();

        public Fixture SeedAgent(
            Guid agentId,
            AgentRuntimeStatusReport? runtimeStatus = null,
            Exception? runtimeStatusError = null)
        {
            _seededAgents.Add(agentId);
            if (runtimeStatus is not null)
            {
                _agentReports[agentId] = runtimeStatus;
            }
            if (runtimeStatusError is not null)
            {
                _agentErrors[agentId] = runtimeStatusError;
            }
            return this;
        }

        public Fixture SeedSubUnit(
            Guid unitId,
            AgentRuntimeStatusReport? runtimeStatus = null,
            Exception? runtimeStatusError = null)
        {
            _seededSubUnits.Add(unitId);
            if (runtimeStatus is not null)
            {
                _unitReports[unitId] = runtimeStatus;
            }
            if (runtimeStatusError is not null)
            {
                _unitErrors[unitId] = runtimeStatusError;
            }
            return this;
        }

        public Fixture SeedHumanMember(Guid humanId, string displayName)
        {
            _humanDisplayNames[humanId] = displayName;
            _membershipStore.Seed(UnitId, humanId,
                Array.Empty<string>(),
                Array.Empty<string>(),
                Array.Empty<string>());
            return this;
        }

        public BuiltFixture Build()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            // Bind the database name OUTSIDE the delegate so every DbContext
            // created from this ServiceCollection lands on the same
            // in-memory database. Capturing Guid.NewGuid() inside the
            // delegate gives each context its own DB (the options delegate
            // re-runs on every resolution) and the agent seed becomes
            // invisible to the registry's later scope.
            var dbName = "sv-livestatus-tests-" + Guid.NewGuid().ToString("N");
            services.AddDbContext<SpringDbContext>(opt => opt
                .UseInMemoryDatabase(dbName)
                .ConfigureWarnings(w => w.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning)));

            // Register ITenantContext so SpringDbContext picks up the test
            // tenant on every DI scope. Without this DI falls back to the
            // single-arg constructor (OssTenantIds.Default), and the
            // tenant query filter on AgentDefinitions / UnitDefinitions
            // would mask the seeded rows from ResolveKindAsync. We use a
            // concrete StaticTenantContext rather than a substitute so EF
            // can evaluate the captured query-filter closure deterministically.
            ITenantContext tenantContext = new Cvoya.Spring.Dapr.Tenancy.StaticTenantContext(TenantId);
            services.AddScoped<ITenantContext>(_ => tenantContext);

            var identityResolver = Substitute.For<IHumanIdentityResolver>();
            foreach (var (id, name) in _humanDisplayNames)
            {
                identityResolver
                    .GetDisplayNameAsync(id, Arg.Any<CancellationToken>())
                    .Returns(name);
            }
            services.AddScoped<IHumanIdentityResolver>(_ => identityResolver);

            // Permissive directory-read enforcer so the tests exercise the
            // live_status path without authz noise.
            var enforcer = Substitute.For<IUnitPolicyEnforcer>();
            enforcer
                .EvaluateUnitDirectoryReadAsync(
                    Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(PolicyDecision.Allowed);
            services.AddScoped<IUnitPolicyEnforcer>(_ => enforcer);

            var participantResolver = Substitute.For<IParticipantDisplayNameResolver>();
            participantResolver
                .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => new ValueTask<string>(call.ArgAt<string>(0)));
            services.AddScoped<IParticipantDisplayNameResolver>(_ => participantResolver);

            var membershipRepo = Substitute.For<IUnitMembershipRepository>();
            membershipRepo
                .ListByUnitAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(_seededAgents.Select(a => new UnitMembership(UnitId, a, null, null)).ToList());
            membershipRepo
                .ListByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    var agentId = call.ArgAt<Guid>(0);
                    if (_seededAgents.Contains(agentId))
                    {
                        return new List<UnitMembership>
                        {
                            new(UnitId, agentId, null, null),
                        };
                    }
                    return new List<UnitMembership>();
                });
            services.AddScoped<IUnitMembershipRepository>(_ => membershipRepo);

            var subunitRepo = Substitute.For<IUnitSubunitMembershipRepository>();
            services.AddScoped<IUnitSubunitMembershipRepository>(_ => subunitRepo);

            var sp = services.BuildServiceProvider();
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();

            // Seed AgentDefinitions / UnitDefinitions in the in-memory
            // db so ResolveKindAsync can disambiguate kind.
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
                foreach (var subUnitId in _seededSubUnits)
                {
                    db.UnitDefinitions.Add(new UnitDefinitionEntity
                    {
                        Id = subUnitId,
                        TenantId = TenantId,
                        DisplayName = $"unit-{subUnitId:N}",
                        Description = "test unit",
                    });
                }
                db.SaveChanges();
            }

            var memberGraph = new InMemoryUnitMemberGraphStore();
            foreach (var agentId in _seededAgents)
            {
                memberGraph.SeedAgentMembers(UnitId, agentId);
            }
            foreach (var subUnitId in _seededSubUnits)
            {
                memberGraph.SeedSubunitChildren(UnitId, subUnitId);
            }

            var expertiseStore = Substitute.For<IExpertiseStore>();
            expertiseStore
                .GetDomainsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
                .Returns(Array.Empty<ExpertiseDomain>());

            // Actor proxy factory: route each seeded agent / unit id to a
            // substitute proxy whose GetRuntimeStatusAsync returns the
            // configured report (or throws the configured exception, when
            // the test pins the error tolerance).
            var actorProxyFactory = Substitute.For<IActorProxyFactory>();
            foreach (var agentId in _seededAgents)
            {
                var actorId = new ActorId(GuidFormatter.Format(agentId));
                var proxy = Substitute.For<IAgentActor>();
                if (_agentReports.TryGetValue(agentId, out var report))
                {
                    proxy.GetRuntimeStatusAsync(Arg.Any<CancellationToken>())
                        .Returns(report);
                }
                if (_agentErrors.TryGetValue(agentId, out var err))
                {
                    proxy.GetRuntimeStatusAsync(Arg.Any<CancellationToken>())
                        .ThrowsAsyncForAnyArgs(err);
                }
                actorProxyFactory
                    .CreateActorProxy<IAgentActor>(
                        Arg.Is<ActorId>(a => a.GetId() == actorId.GetId()),
                        Arg.Any<string>())
                    .Returns(proxy);
            }
            foreach (var unitId in _seededSubUnits)
            {
                var actorId = new ActorId(GuidFormatter.Format(unitId));
                var proxy = Substitute.For<IUnitActor>();
                if (_unitReports.TryGetValue(unitId, out var report))
                {
                    proxy.GetRuntimeStatusAsync(Arg.Any<CancellationToken>())
                        .Returns(report);
                }
                if (_unitErrors.TryGetValue(unitId, out var err))
                {
                    proxy.GetRuntimeStatusAsync(Arg.Any<CancellationToken>())
                        .ThrowsAsyncForAnyArgs(err);
                }
                actorProxyFactory
                    .CreateActorProxy<IUnitActor>(
                        Arg.Is<ActorId>(a => a.GetId() == actorId.GetId()),
                        Arg.Any<string>())
                    .Returns(proxy);
            }

            // #3089: these tests pin live_status, not roles, so an empty
            // member-role seam is sufficient — agent entries surface no
            // roles, which the live-status assertions do not inspect.
            var memberRoleDirectory = new InMemoryUnitMemberRoleDirectory();

            var registry = new SvDirectorySkillRegistry(
                scopeFactory,
                memberGraph,
                _membershipStore,
                memberRoleDirectory,
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

        public async Task<JsonElement> GetStatusAsJsonAsync(Guid targetId)
        {
            var args = JsonDocument.Parse($$"""{ "uuid": "{{GuidFormatter.Format(targetId)}}" }""").RootElement;
            var ctx = new ToolCallContext(
                CallerId: GuidFormatter.Format(CallerId),
                CallerKind: SvDirectorySkillRegistry.KindHuman,
                ThreadId: Guid.NewGuid().ToString("N"));

            return await _registry.InvokeAsync(
                SvDirectorySkillRegistry.GetStatusTool,
                args,
                ctx,
                TestContext.Current.CancellationToken);
        }
    }
}
