// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Observability;
using Cvoya.Spring.Dapr.Tenancy;

using Microsoft.EntityFrameworkCore;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="InteractionsQueryService"/> — the EF-backed
/// projection that powers the portal's Interactions visualization (#2867).
/// Seeds the <c>messages</c> table directly and asserts the aggregation
/// shape downstream surfaces depend on: nodes / edges / timeline /
/// truncation. Tenant scoping flows from the
/// <see cref="SpringDbContext"/>'s tenant query filter; the tenant-
/// isolation test wires a second context against the same in-memory
/// database to verify cross-tenant rows are invisible.
/// </summary>
public class InteractionsQueryServiceTests : IDisposable
{
    private static readonly Guid TenantId = new("bbbb2002-3333-4444-5555-000000000001");
    private static readonly Guid OtherTenantId = new("bbbb2002-3333-4444-5555-000000000002");

    private readonly DbContextOptions<SpringDbContext> _dbOptions;
    private readonly SpringDbContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly IParticipantDisplayNameResolver _resolver;
    private readonly IUnitMembershipRepository _unitMemberships;
    private readonly IUnitSubunitMembershipRepository _unitSubunitMemberships;
    private readonly IUnitHumanMembershipStore _unitHumanMemberships;

    public InteractionsQueryServiceTests()
    {
        _dbOptions = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase($"InteractionsQueryTest-{Guid.NewGuid()}")
            .Options;
        _tenantContext = new StaticTenantContext(TenantId);
        _db = new SpringDbContext(_dbOptions, _tenantContext);

        _resolver = Substitute.For<IParticipantDisplayNameResolver>();
        // The aggregation passes scheme:hex into the resolver. Mirror it
        // back so the test can assert on a deterministic "<scheme>:<hex>"
        // label without spinning up the real EF-backed implementation.
        _resolver
            .ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<string>(ci.Arg<string>()));

        // Default empty membership stubs. Tests that exercise the unit
        // scope override these via Returns() in the test body.
        _unitMemberships = Substitute.For<IUnitMembershipRepository>();
        _unitMemberships
            .ListByUnitAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UnitMembership>());
        _unitSubunitMemberships = Substitute.For<IUnitSubunitMembershipRepository>();
        _unitSubunitMemberships
            .ListByParentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UnitSubunitMembership>());
        _unitHumanMemberships = Substitute.For<IUnitHumanMembershipStore>();
        _unitHumanMemberships
            .ListByUnitAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UnitHumanMembership>());
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private InteractionsQueryService BuildService() => new(
        _db,
        _resolver,
        _unitMemberships,
        _unitSubunitMemberships,
        _unitHumanMemberships);

    private static readonly DateTimeOffset Base = DateTimeOffset.Parse("2026-05-27T12:00:00Z");

    [Fact]
    public async Task GetAsync_NoMessages_ReturnsEmptyGraph()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = BuildService();

        var graph = await svc.GetAsync(new InteractionsQueryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: null,
            Neighbours: 2, Bucket: InteractionsBucket.Hour, Cap: 50),
            ct);

        graph.Nodes.ShouldBeEmpty();
        graph.Edges.ShouldBeEmpty();
        graph.Timeline.ShouldBeEmpty();
        graph.Truncated.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_TwoMessagesOneEdge_AggregatesNodesAndEdges()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();

        await SeedMessageAsync(
            sender: (Address.AgentScheme, ada),
            recipient: (Address.AgentScheme, grace),
            sentAt: Base.AddMinutes(1));
        await SeedMessageAsync(
            sender: (Address.AgentScheme, ada),
            recipient: (Address.AgentScheme, grace),
            sentAt: Base.AddMinutes(2));

        var svc = BuildService();
        var graph = await svc.GetAsync(new InteractionsQueryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: null,
            Neighbours: 2, Bucket: InteractionsBucket.Hour, Cap: 50),
            ct);

        graph.Nodes.Count.ShouldBe(2);
        var adaNode = graph.Nodes.Single(n => n.Id == GuidFormatter.Format(ada));
        adaNode.Kind.ShouldBe(Address.AgentScheme);
        adaNode.Sent.ShouldBe(2);
        adaNode.Received.ShouldBe(0);

        var graceNode = graph.Nodes.Single(n => n.Id == GuidFormatter.Format(grace));
        graceNode.Kind.ShouldBe(Address.AgentScheme);
        graceNode.Sent.ShouldBe(0);
        graceNode.Received.ShouldBe(2);

        graph.Edges.Count.ShouldBe(1);
        var edge = graph.Edges.Single();
        edge.FromId.ShouldBe(GuidFormatter.Format(ada));
        edge.ToId.ShouldBe(GuidFormatter.Format(grace));
        edge.Count.ShouldBe(2);
        edge.Channels.ShouldContain(Address.AgentScheme);
    }

    [Fact]
    public async Task GetAsync_TimeBucketing_AlignsToHourBoundary()
    {
        // Three messages spread across two hour buckets. The timeline
        // should zero-fill between the window's first and last buckets
        // (here that's just the two observed buckets).
        var ct = TestContext.Current.CancellationToken;
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();

        await SeedMessageAsync((Address.AgentScheme, ada), (Address.AgentScheme, grace), Base.AddMinutes(5));
        await SeedMessageAsync((Address.AgentScheme, ada), (Address.AgentScheme, grace), Base.AddMinutes(15));
        await SeedMessageAsync((Address.AgentScheme, ada), (Address.AgentScheme, grace), Base.AddHours(1).AddMinutes(5));

        var svc = BuildService();
        var graph = await svc.GetAsync(new InteractionsQueryFilters(
            Since: Base, Until: Base.AddHours(2),
            Unit: null, Participant: null,
            Neighbours: 2, Bucket: InteractionsBucket.Hour, Cap: 50),
            ct);

        graph.Timeline.Count.ShouldBe(2);
        graph.Timeline[0].Bucket.ShouldBe(Base);
        graph.Timeline[0].Sent.ShouldBe(2);
        graph.Timeline[1].Bucket.ShouldBe(Base.AddHours(1));
        graph.Timeline[1].Sent.ShouldBe(1);

        // ByKind breakdown: all three sends are from an agent.
        graph.Timeline[0].ByKind[Address.AgentScheme].ShouldBe(2);
        graph.Timeline[1].ByKind[Address.AgentScheme].ShouldBe(1);
        graph.Timeline[0].ByKind[Address.UnitScheme].ShouldBe(0);
    }

    [Fact]
    public async Task GetAsync_DayBucket_AlignsToMidnightUtc()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();

        var dayStart = new DateTimeOffset(2026, 5, 27, 0, 0, 0, TimeSpan.Zero);
        await SeedMessageAsync((Address.AgentScheme, ada), (Address.AgentScheme, grace), dayStart.AddHours(8));
        await SeedMessageAsync((Address.AgentScheme, ada), (Address.AgentScheme, grace), dayStart.AddHours(20));

        var svc = BuildService();
        var graph = await svc.GetAsync(new InteractionsQueryFilters(
            Since: dayStart, Until: dayStart.AddDays(1),
            Unit: null, Participant: null,
            Neighbours: 2, Bucket: InteractionsBucket.Day, Cap: 50),
            ct);

        graph.Timeline.Count.ShouldBe(1);
        graph.Timeline[0].Bucket.ShouldBe(dayStart);
        graph.Timeline[0].Sent.ShouldBe(2);
    }

    [Fact]
    public async Task GetAsync_Neighbours0_OnlyEdgesTouchingFocusNode()
    {
        // Topology: ada → grace, grace → hopper, hopper → lovelace.
        // Focus = grace; hops = 0 means only edges where grace is one
        // of the two endpoints.
        var ct = TestContext.Current.CancellationToken;
        var (ada, grace, hopper, lovelace) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await SeedMessageAsync((Address.AgentScheme, ada), (Address.AgentScheme, grace), Base.AddMinutes(1));
        await SeedMessageAsync((Address.AgentScheme, grace), (Address.AgentScheme, hopper), Base.AddMinutes(2));
        await SeedMessageAsync((Address.AgentScheme, hopper), (Address.AgentScheme, lovelace), Base.AddMinutes(3));

        var svc = BuildService();
        var graph = await svc.GetAsync(new InteractionsQueryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: grace,
            Neighbours: 0, Bucket: InteractionsBucket.Hour, Cap: 50),
            ct);

        graph.Edges.Count.ShouldBe(2); // ada → grace and grace → hopper
        graph.Edges.ShouldContain(e => e.FromId == GuidFormatter.Format(ada) && e.ToId == GuidFormatter.Format(grace));
        graph.Edges.ShouldContain(e => e.FromId == GuidFormatter.Format(grace) && e.ToId == GuidFormatter.Format(hopper));
        graph.Nodes.Select(n => n.Id).ShouldNotContain(GuidFormatter.Format(lovelace));
    }

    [Fact]
    public async Task GetAsync_Neighbours1_IncludesDirectNeighbours()
    {
        // Same topology. Focus = grace; hops = 1 expands to include
        // edges where ada or hopper are one endpoint (they're grace's
        // direct neighbours).
        var ct = TestContext.Current.CancellationToken;
        var (ada, grace, hopper, lovelace) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await SeedMessageAsync((Address.AgentScheme, ada), (Address.AgentScheme, grace), Base.AddMinutes(1));
        await SeedMessageAsync((Address.AgentScheme, grace), (Address.AgentScheme, hopper), Base.AddMinutes(2));
        await SeedMessageAsync((Address.AgentScheme, hopper), (Address.AgentScheme, lovelace), Base.AddMinutes(3));

        var svc = BuildService();
        var graph = await svc.GetAsync(new InteractionsQueryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: grace,
            Neighbours: 1, Bucket: InteractionsBucket.Hour, Cap: 50),
            ct);

        // hops=1 expands the focus {grace} to {grace, ada, hopper}; the
        // edge hopper→lovelace touches a non-scope endpoint and is
        // excluded.
        graph.Edges.Count.ShouldBe(2);
        graph.Nodes.Select(n => n.Id).ShouldNotContain(GuidFormatter.Format(lovelace));
    }

    [Fact]
    public async Task GetAsync_Neighbours2_IncludesSecondDegreeNeighbours()
    {
        // Topology: ada → grace, grace → hopper, hopper → lovelace.
        // Focus = grace; hops = 2 expands {grace} → {grace, ada, hopper}
        // → {grace, ada, hopper, lovelace}. All three edges are in scope.
        var ct = TestContext.Current.CancellationToken;
        var (ada, grace, hopper, lovelace) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await SeedMessageAsync((Address.AgentScheme, ada), (Address.AgentScheme, grace), Base.AddMinutes(1));
        await SeedMessageAsync((Address.AgentScheme, grace), (Address.AgentScheme, hopper), Base.AddMinutes(2));
        await SeedMessageAsync((Address.AgentScheme, hopper), (Address.AgentScheme, lovelace), Base.AddMinutes(3));

        var svc = BuildService();
        var graph = await svc.GetAsync(new InteractionsQueryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: grace,
            Neighbours: 2, Bucket: InteractionsBucket.Hour, Cap: 50),
            ct);

        graph.Edges.Count.ShouldBe(3);
        graph.Nodes.Count.ShouldBe(4);
    }

    [Fact]
    public async Task GetAsync_UnitScope_ExpandsToMemberAgents()
    {
        // Verifies the unit-expansion that drives the portal Interactions
        // view: when the operator scopes by a unit, the filter resolves
        // the unit's agent / human / sub-unit members so inter-member
        // traffic — the bulk of a unit's activity — shows up in the
        // graph. Without this expansion a bare-GUID match against
        // messages.SenderId/RecipientId misses agent-to-agent edges
        // inside the unit (the bug operators reported on the Magazine
        // Editor unit).
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        var managing = Guid.NewGuid();
        var staffWriter = Guid.NewGuid();
        var outsider = Guid.NewGuid();

        // Inter-member traffic — neither endpoint is the unit GUID,
        // but both are members of the scoped unit.
        await SeedMessageAsync((Address.AgentScheme, managing), (Address.AgentScheme, staffWriter), Base.AddMinutes(1));
        // Out-of-unit edge — should be excluded with neighbours = 0.
        await SeedMessageAsync((Address.AgentScheme, outsider), (Address.AgentScheme, outsider), Base.AddMinutes(2));

        _unitMemberships
            .ListByUnitAsync(unitId, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new UnitMembership(unitId, managing),
                new UnitMembership(unitId, staffWriter),
            });

        var svc = BuildService();
        var graph = await svc.GetAsync(new InteractionsQueryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: unitId, Participant: null,
            Neighbours: 0, Bucket: InteractionsBucket.Hour, Cap: 50),
            ct);

        graph.Edges.Count.ShouldBe(1);
        var edge = graph.Edges.Single();
        edge.FromId.ShouldBe(GuidFormatter.Format(managing));
        edge.ToId.ShouldBe(GuidFormatter.Format(staffWriter));
        graph.Nodes.Select(n => n.Id).ShouldNotContain(GuidFormatter.Format(outsider));
    }

    [Fact]
    public async Task GetAsync_UnitScope_WalksSubunitsAndIncludesHumans()
    {
        // Sub-units are walked transitively: a unit's scope includes
        // every member agent on every sub-unit, plus every human member
        // at any level. Catches the deep-nesting case (Magazine Editor →
        // Editorial Desk → managing-editor) that a single-level
        // membership read would miss.
        var ct = TestContext.Current.CancellationToken;
        var parentUnit = Guid.NewGuid();
        var childUnit = Guid.NewGuid();
        var childAgent = Guid.NewGuid();
        var humanMember = Guid.NewGuid();
        var outsider = Guid.NewGuid();

        await SeedMessageAsync(
            (Address.AgentScheme, childAgent),
            (Address.HumanScheme, humanMember),
            Base.AddMinutes(1));
        await SeedMessageAsync(
            (Address.AgentScheme, outsider),
            (Address.AgentScheme, outsider),
            Base.AddMinutes(2));

        _unitSubunitMemberships
            .ListByParentAsync(parentUnit, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new UnitSubunitMembership(parentUnit, childUnit),
            });
        _unitMemberships
            .ListByUnitAsync(childUnit, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new UnitMembership(childUnit, childAgent),
            });
        _unitHumanMemberships
            .ListByUnitAsync(parentUnit, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new UnitHumanMembership(
                    MembershipId: Guid.NewGuid(),
                    HumanId: humanMember,
                    Roles: Array.Empty<string>(),
                    Expertise: Array.Empty<string>(),
                    Notifications: Array.Empty<string>()),
            });

        var svc = BuildService();
        var graph = await svc.GetAsync(new InteractionsQueryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: parentUnit, Participant: null,
            Neighbours: 0, Bucket: InteractionsBucket.Hour, Cap: 50),
            ct);

        graph.Edges.Count.ShouldBe(1);
        graph.Edges.Single().ToId.ShouldBe(GuidFormatter.Format(humanMember));
        graph.Nodes.Select(n => n.Id).ShouldNotContain(GuidFormatter.Format(outsider));
    }

    [Fact]
    public async Task GetAsync_TopCapTruncation_KeepsTopByActivity()
    {
        var ct = TestContext.Current.CancellationToken;

        var hot = Guid.NewGuid();
        var med = Guid.NewGuid();
        var cold1 = Guid.NewGuid();
        var cold2 = Guid.NewGuid();
        var cold3 = Guid.NewGuid();

        // hot ↔ med traffic: 5 messages each way, total activity = 10
        for (var i = 0; i < 5; i++)
        {
            await SeedMessageAsync((Address.AgentScheme, hot), (Address.AgentScheme, med), Base.AddMinutes(i));
            await SeedMessageAsync((Address.AgentScheme, med), (Address.AgentScheme, hot), Base.AddMinutes(i + 30));
        }
        // cold1/cold2/cold3: 1 message each at the same time
        await SeedMessageAsync((Address.AgentScheme, cold1), (Address.AgentScheme, cold2), Base.AddMinutes(10));
        await SeedMessageAsync((Address.AgentScheme, cold2), (Address.AgentScheme, cold3), Base.AddMinutes(11));

        var svc = BuildService();
        var graph = await svc.GetAsync(new InteractionsQueryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: null,
            Neighbours: 2, Bucket: InteractionsBucket.Hour, Cap: 2),
            ct);

        // Cap = 2 means we keep the top 2 by sent + received. hot has
        // 5 sent + 5 received = 10; med has 5 sent + 5 received = 10;
        // cold* nodes each have 1 or 2 — all dropped.
        graph.Nodes.Count.ShouldBe(2);
        graph.Nodes.Select(n => n.Id).ShouldBe(
            new[] { GuidFormatter.Format(hot), GuidFormatter.Format(med) },
            ignoreOrder: true);

        // Truncation payload: total = 5 distinct nodes; kept = 2.
        graph.Truncated.ShouldNotBeNull();
        graph.Truncated!.Total.ShouldBe(5);
        graph.Truncated.Kept.ShouldBe(2);

        // Edges referencing dropped nodes are dropped too.
        graph.Edges.ShouldAllBe(e =>
            (e.FromId == GuidFormatter.Format(hot) || e.FromId == GuidFormatter.Format(med)) &&
            (e.ToId == GuidFormatter.Format(hot) || e.ToId == GuidFormatter.Format(med)));
    }

    [Fact]
    public async Task GetAsync_CountFitsWithinCap_TruncationPayloadOmitted()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();

        await SeedMessageAsync((Address.AgentScheme, ada), (Address.AgentScheme, grace), Base.AddMinutes(1));

        var svc = BuildService();
        var graph = await svc.GetAsync(new InteractionsQueryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: null,
            Neighbours: 2, Bucket: InteractionsBucket.Hour, Cap: 50),
            ct);

        graph.Nodes.Count.ShouldBe(2);
        graph.Truncated.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_NoCap_NeverTruncates()
    {
        var ct = TestContext.Current.CancellationToken;

        // Seed 60 distinct sender/recipient pairs. Cap = null means
        // truncation never applies regardless of node count.
        for (var i = 0; i < 60; i++)
        {
            var s = Guid.NewGuid();
            var r = Guid.NewGuid();
            await SeedMessageAsync((Address.AgentScheme, s), (Address.AgentScheme, r), Base.AddSeconds(i));
        }

        var svc = BuildService();
        var graph = await svc.GetAsync(new InteractionsQueryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: null,
            Neighbours: 2, Bucket: InteractionsBucket.Hour, Cap: null),
            ct);

        graph.Nodes.Count.ShouldBe(120);
        graph.Truncated.ShouldBeNull();
    }

    [Fact]
    public async Task GetAsync_ConnectorRecipient_FilteredOut()
    {
        // Per ADR-0048 a connector is provenance-only — the router rejects
        // sends to a connector recipient. Defensive: even if a legacy row
        // were to land in the messages table, the aggregation must never
        // emit an edge with a connector ToId.
        var ct = TestContext.Current.CancellationToken;
        var ada = Guid.NewGuid();
        var connector = Guid.NewGuid();
        var grace = Guid.NewGuid();

        // Inbound webhook event from a connector — legitimate (connector
        // appears as From; the recipient is an agent).
        await SeedMessageAsync((Address.ConnectorScheme, connector), (Address.AgentScheme, ada), Base.AddMinutes(1));
        // Synthetic row with connector as recipient — the defensive
        // filter must drop this.
        await SeedMessageAsync((Address.AgentScheme, ada), (Address.ConnectorScheme, connector), Base.AddMinutes(2));
        // Routine agent → agent traffic.
        await SeedMessageAsync((Address.AgentScheme, ada), (Address.AgentScheme, grace), Base.AddMinutes(3));

        var svc = BuildService();
        var graph = await svc.GetAsync(new InteractionsQueryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: null,
            Neighbours: 2, Bucket: InteractionsBucket.Hour, Cap: 50),
            ct);

        // No edge has a connector ToId.
        var nodeKindById = graph.Nodes.ToDictionary(n => n.Id, n => n.Kind);
        graph.Edges
            .All(e => !nodeKindById.TryGetValue(e.ToId, out var k) || k != Address.ConnectorScheme)
            .ShouldBeTrue("no edge should have a connector ToId");

        // The connector → ada edge is intact (ADR-0048 allows
        // connector-from rows).
        graph.Edges.ShouldContain(e =>
            e.FromId == GuidFormatter.Format(connector) && e.ToId == GuidFormatter.Format(ada));

        // The synthetic ada → connector row is gone.
        graph.Edges.ShouldNotContain(e =>
            e.FromId == GuidFormatter.Format(ada) && e.ToId == GuidFormatter.Format(connector));
    }

    [Fact]
    public async Task GetAsync_OtherTenantRows_Excluded()
    {
        // Seed a message under the "other" tenant id; query under the
        // current tenant. The EF query filter on SpringDbContext is what
        // enforces tenant isolation — this test catches any future
        // refactor that bypasses it (e.g. an over-eager IgnoreQueryFilters
        // call).
        var ct = TestContext.Current.CancellationToken;
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();

        await SeedMessageAsync(
            sender: (Address.AgentScheme, ada),
            recipient: (Address.AgentScheme, grace),
            sentAt: Base.AddMinutes(1),
            tenantOverride: OtherTenantId);

        var svc = BuildService();
        var graph = await svc.GetAsync(new InteractionsQueryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: null,
            Neighbours: 2, Bucket: InteractionsBucket.Hour, Cap: 50),
            ct);

        graph.Nodes.ShouldBeEmpty();
        graph.Edges.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAsync_OutsideWindow_Excluded()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();

        await SeedMessageAsync((Address.AgentScheme, ada), (Address.AgentScheme, grace), Base.AddMinutes(-5));
        await SeedMessageAsync((Address.AgentScheme, ada), (Address.AgentScheme, grace), Base.AddHours(2));

        var svc = BuildService();
        var graph = await svc.GetAsync(new InteractionsQueryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: null,
            Neighbours: 2, Bucket: InteractionsBucket.Hour, Cap: 50),
            ct);

        graph.Nodes.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetAsync_NonDomainMessages_Excluded()
    {
        // Only Domain messages are interactions per ADR-0030. Control
        // envelopes (Cancel, HealthCheck, StatusQuery) persist for
        // debugging but never count toward the graph — they are
        // platform plumbing, not visible interactions.
        var ct = TestContext.Current.CancellationToken;
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();

        await SeedMessageAsync(
            (Address.AgentScheme, ada), (Address.AgentScheme, grace), Base.AddMinutes(1),
            messageType: "Cancel");
        await SeedMessageAsync(
            (Address.AgentScheme, ada), (Address.AgentScheme, grace), Base.AddMinutes(2),
            messageType: "Domain");

        var svc = BuildService();
        var graph = await svc.GetAsync(new InteractionsQueryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: null,
            Neighbours: 2, Bucket: InteractionsBucket.Hour, Cap: 50),
            ct);

        graph.Edges.Single().Count.ShouldBe(1);
    }

    [Fact]
    public async Task GetAsync_DisplayName_ResolvedViaResolver()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();

        // Override the default mirror-back resolver with named labels.
        _resolver
            .ResolveAsync(Arg.Is<string>(s => s.Contains(GuidFormatter.Format(ada))), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>("Ada"));
        _resolver
            .ResolveAsync(Arg.Is<string>(s => s.Contains(GuidFormatter.Format(grace))), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<string>("Grace"));

        await SeedMessageAsync((Address.AgentScheme, ada), (Address.AgentScheme, grace), Base.AddMinutes(1));

        var svc = BuildService();
        var graph = await svc.GetAsync(new InteractionsQueryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: null,
            Neighbours: 2, Bucket: InteractionsBucket.Hour, Cap: 50),
            ct);

        graph.Nodes.Single(n => n.Id == GuidFormatter.Format(ada)).DisplayName.ShouldBe("Ada");
        graph.Nodes.Single(n => n.Id == GuidFormatter.Format(grace)).DisplayName.ShouldBe("Grace");
    }

    [Fact]
    public async Task GetAsync_EdgeChannels_CollectsRecipientSchemes()
    {
        // An edge can carry messages of mixed recipient kinds when the
        // same id wears multiple hats. The Channels list deduplicates
        // and orders the observed schemes so downstream consumers can
        // diff snapshots without hash-order surprise.
        var ct = TestContext.Current.CancellationToken;
        var ada = Guid.NewGuid();
        var eng = Guid.NewGuid();

        await SeedMessageAsync((Address.AgentScheme, ada), (Address.UnitScheme, eng), Base.AddMinutes(1));
        await SeedMessageAsync((Address.AgentScheme, ada), (Address.UnitScheme, eng), Base.AddMinutes(2));

        var svc = BuildService();
        var graph = await svc.GetAsync(new InteractionsQueryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: null,
            Neighbours: 2, Bucket: InteractionsBucket.Hour, Cap: 50),
            ct);

        var edge = graph.Edges.Single();
        edge.Channels.ShouldBe(new[] { Address.UnitScheme });
    }

    // ---- GetHistoryAsync (rewind mode, #2872) ----------------------------
    // These tests live in this file because the history path reuses the
    // same projection, scope, and cap helpers as the snapshot — keeping
    // both surfaces in one suite makes regressions in shared helpers fail
    // loudly on every assertion that touches them.

    [Fact]
    public async Task GetHistoryAsync_UnitScope_ExpandsToMembersAndReturnsEdges()
    {
        // Mirrors the snapshot-side GetAsync_UnitScope_ExpandsToMemberAgents
        // for the rewind path. Operator scopes the Interactions view to a
        // unit; the inter-member messages (the bulk of the unit's traffic)
        // must surface as edges + pulses so the rewind canvas paints
        // lines between members and animates pulses along them. Without
        // the unit-expansion the unit's bare GUID never appears as
        // sender/recipient and the history response strips inter-member
        // traffic — the symptom operators reported as "nodes but no
        // arrows during rewind".
        var ct = TestContext.Current.CancellationToken;
        var unitId = Guid.NewGuid();
        var managing = Guid.NewGuid();
        var staffWriter = Guid.NewGuid();
        var outsider = Guid.NewGuid();

        await SeedMessageAsync((Address.AgentScheme, managing), (Address.AgentScheme, staffWriter), Base.AddMinutes(1));
        await SeedMessageAsync((Address.AgentScheme, staffWriter), (Address.AgentScheme, managing), Base.AddMinutes(2));
        await SeedMessageAsync((Address.AgentScheme, outsider), (Address.AgentScheme, outsider), Base.AddMinutes(3));

        _unitMemberships
            .ListByUnitAsync(unitId, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new UnitMembership(unitId, managing),
                new UnitMembership(unitId, staffWriter),
            });

        var svc = BuildService();
        var history = await svc.GetHistoryAsync(new InteractionsHistoryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: unitId, Participant: null,
            Neighbours: 0, Cap: 50, MaxPulses: 5000),
            ct);

        history.Edges.Count.ShouldBe(2);
        history.Edges.ShouldContain(e =>
            e.FromId == GuidFormatter.Format(managing) &&
            e.ToId == GuidFormatter.Format(staffWriter));
        history.Edges.ShouldContain(e =>
            e.FromId == GuidFormatter.Format(staffWriter) &&
            e.ToId == GuidFormatter.Format(managing));
        history.Pulses.Count.ShouldBe(2);
        history.Nodes.Select(n => n.Id).ShouldNotContain(GuidFormatter.Format(outsider));
    }

    [Fact]
    public async Task GetHistoryAsync_NoMessages_ReturnsEmptyHistory()
    {
        var ct = TestContext.Current.CancellationToken;
        var svc = BuildService();

        var history = await svc.GetHistoryAsync(new InteractionsHistoryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: null,
            Neighbours: 2, Cap: 50, MaxPulses: 5000),
            ct);

        history.Nodes.ShouldBeEmpty();
        history.Edges.ShouldBeEmpty();
        history.Pulses.ShouldBeEmpty();
        history.Truncated.ShouldBeNull();
    }

    [Fact]
    public async Task GetHistoryAsync_OnePulsePerMessage_NeverCoalesced()
    {
        // Five messages on the same (from, to) edge → five pulses. The
        // snapshot path would collapse these to one edge with count = 5;
        // history keeps each message individually addressable so the
        // rewind UI can animate them one by one.
        var ct = TestContext.Current.CancellationToken;
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
        {
            await SeedMessageAsync(
                (Address.AgentScheme, ada),
                (Address.AgentScheme, grace),
                Base.AddMinutes(i + 1));
        }

        var svc = BuildService();
        var history = await svc.GetHistoryAsync(new InteractionsHistoryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: null,
            Neighbours: 2, Cap: 50, MaxPulses: 5000),
            ct);

        history.Pulses.Count.ShouldBe(5);
        history.Pulses.Select(p => p.FromId).ShouldAllBe(id => id == GuidFormatter.Format(ada));
        history.Pulses.Select(p => p.ToId).ShouldAllBe(id => id == GuidFormatter.Format(grace));
        history.Edges.Count.ShouldBe(1);
        history.Edges.Single().Count.ShouldBe(5);
    }

    [Fact]
    public async Task GetHistoryAsync_PulsesSortedAscending_TieBrokenByMessageId()
    {
        // Two messages with the exact same timestamp → ordered by canonical
        // 32-hex message id ascending. We seed the larger id first so the
        // dictionary iteration order would emit it first; the sort enforces
        // (timestamp asc, id asc).
        var ct = TestContext.Current.CancellationToken;
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();

        var sameInstant = Base.AddMinutes(1);
        // Two ids — pick the comparator order deterministically.
        var idA = new Guid("aaaaaaaa-0000-0000-0000-000000000001");
        var idB = new Guid("ffffffff-0000-0000-0000-000000000002");

        await SeedMessageAsync(
            (Address.AgentScheme, ada), (Address.AgentScheme, grace),
            sameInstant, messageId: idB);
        await SeedMessageAsync(
            (Address.AgentScheme, ada), (Address.AgentScheme, grace),
            sameInstant, messageId: idA);

        // Plus one strictly-later message so ordering across timestamps is
        // also exercised.
        var idC = Guid.NewGuid();
        await SeedMessageAsync(
            (Address.AgentScheme, ada), (Address.AgentScheme, grace),
            sameInstant.AddSeconds(1), messageId: idC);

        var svc = BuildService();
        var history = await svc.GetHistoryAsync(new InteractionsHistoryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: null,
            Neighbours: 2, Cap: 50, MaxPulses: 5000),
            ct);

        history.Pulses.Count.ShouldBe(3);
        // First two share the timestamp; idA < idB lexically in 32-hex.
        history.Pulses[0].Id.ShouldBe(GuidFormatter.Format(idA));
        history.Pulses[1].Id.ShouldBe(GuidFormatter.Format(idB));
        history.Pulses[2].Id.ShouldBe(GuidFormatter.Format(idC));
    }

    [Fact]
    public async Task GetHistoryAsync_MaxPulsesExceeded_DropsOldest_PopulatesPulseTruncation()
    {
        // 10 messages, budget = 4 → keep the 4 most recent, report
        // total = 10 / kept = 4. The brief: "drops the OLDEST pulses
        // (keeping the most recent activity)."
        var ct = TestContext.Current.CancellationToken;
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();

        var ids = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
        for (var i = 0; i < 10; i++)
        {
            await SeedMessageAsync(
                (Address.AgentScheme, ada), (Address.AgentScheme, grace),
                Base.AddMinutes(i + 1), messageId: ids[i]);
        }

        var svc = BuildService();
        var history = await svc.GetHistoryAsync(new InteractionsHistoryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: null,
            Neighbours: 2, Cap: 50, MaxPulses: 4),
            ct);

        history.Pulses.Count.ShouldBe(4);
        // The four most recent message ids (indices 6..9) survive.
        history.Pulses.Select(p => p.Id).ShouldBe(
            ids.Skip(6).Select(GuidFormatter.Format).ToList());

        history.Truncated.ShouldNotBeNull();
        history.Truncated!.Pulses.ShouldNotBeNull();
        history.Truncated.Pulses!.Total.ShouldBe(10);
        history.Truncated.Pulses.Kept.ShouldBe(4);
    }

    [Fact]
    public async Task GetHistoryAsync_PulsesFitInBudget_PulseTruncationOmitted()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();

        await SeedMessageAsync(
            (Address.AgentScheme, ada), (Address.AgentScheme, grace),
            Base.AddMinutes(1));

        var svc = BuildService();
        var history = await svc.GetHistoryAsync(new InteractionsHistoryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: null,
            Neighbours: 2, Cap: 50, MaxPulses: 5000),
            ct);

        history.Pulses.Count.ShouldBe(1);
        history.Truncated.ShouldBeNull();
    }

    [Fact]
    public async Task GetHistoryAsync_NodeCapStillApplies_OnHistoryPath()
    {
        // Two "hot" nodes with heavy traffic + three "cold" nodes each
        // touched once. Cap = 2 keeps the two hot nodes; pulses
        // referencing dropped endpoints disappear. The cap counts NODES
        // (not pulses); the two budgets are independent.
        var ct = TestContext.Current.CancellationToken;
        var hot = Guid.NewGuid();
        var med = Guid.NewGuid();
        var cold1 = Guid.NewGuid();
        var cold2 = Guid.NewGuid();
        var cold3 = Guid.NewGuid();

        for (var i = 0; i < 5; i++)
        {
            await SeedMessageAsync(
                (Address.AgentScheme, hot), (Address.AgentScheme, med),
                Base.AddMinutes(i));
            await SeedMessageAsync(
                (Address.AgentScheme, med), (Address.AgentScheme, hot),
                Base.AddMinutes(i + 30));
        }
        await SeedMessageAsync((Address.AgentScheme, cold1), (Address.AgentScheme, cold2), Base.AddMinutes(10));
        await SeedMessageAsync((Address.AgentScheme, cold2), (Address.AgentScheme, cold3), Base.AddMinutes(11));

        var svc = BuildService();
        var history = await svc.GetHistoryAsync(new InteractionsHistoryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: null,
            Neighbours: 2, Cap: 2, MaxPulses: 5000),
            ct);

        history.Nodes.Count.ShouldBe(2);
        history.Nodes.Select(n => n.Id).ShouldBe(
            new[] { GuidFormatter.Format(hot), GuidFormatter.Format(med) },
            ignoreOrder: true);

        history.Truncated.ShouldNotBeNull();
        history.Truncated!.Total.ShouldBe(5);
        history.Truncated.Kept.ShouldBe(2);
        history.Truncated.Pulses.ShouldBeNull(); // node-only branch

        // Every surviving pulse touches one of the two hot endpoints.
        var keptIds = new[] { GuidFormatter.Format(hot), GuidFormatter.Format(med) };
        history.Pulses.ShouldAllBe(p =>
            keptIds.Contains(p.FromId) && keptIds.Contains(p.ToId));
    }

    [Fact]
    public async Task GetHistoryAsync_OtherTenantRows_Excluded()
    {
        var ct = TestContext.Current.CancellationToken;
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();

        await SeedMessageAsync(
            sender: (Address.AgentScheme, ada),
            recipient: (Address.AgentScheme, grace),
            sentAt: Base.AddMinutes(1),
            tenantOverride: OtherTenantId);

        var svc = BuildService();
        var history = await svc.GetHistoryAsync(new InteractionsHistoryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: null,
            Neighbours: 2, Cap: 50, MaxPulses: 5000),
            ct);

        history.Nodes.ShouldBeEmpty();
        history.Pulses.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetHistoryAsync_ConnectorRecipient_NoConnectorToIdOnPulse()
    {
        // The defensive ADR-0048 filter applies on the history path too:
        // a synthetic agent → connector row would be dropped before any
        // pulse is emitted. Connector → agent is legitimate (provenance).
        var ct = TestContext.Current.CancellationToken;
        var ada = Guid.NewGuid();
        var connector = Guid.NewGuid();

        await SeedMessageAsync(
            (Address.ConnectorScheme, connector), (Address.AgentScheme, ada),
            Base.AddMinutes(1));
        await SeedMessageAsync(
            (Address.AgentScheme, ada), (Address.ConnectorScheme, connector),
            Base.AddMinutes(2));

        var svc = BuildService();
        var history = await svc.GetHistoryAsync(new InteractionsHistoryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: null,
            Neighbours: 2, Cap: 50, MaxPulses: 5000),
            ct);

        history.Pulses.Count.ShouldBe(1);
        var pulse = history.Pulses.Single();
        pulse.FromId.ShouldBe(GuidFormatter.Format(connector));
        pulse.ToId.ShouldBe(GuidFormatter.Format(ada));

        // No pulse should ever name a connector as the recipient.
        var connectorHex = GuidFormatter.Format(connector);
        history.Pulses.ShouldNotContain(p => p.ToId == connectorHex);
    }

    [Fact]
    public async Task GetHistoryAsync_WindowBounds_SinceInclusiveUntilExclusive()
    {
        // Seed three messages: one before the window, one at the
        // `since` boundary (should be included — inclusive), one at the
        // `until` boundary (should be excluded — exclusive).
        var ct = TestContext.Current.CancellationToken;
        var ada = Guid.NewGuid();
        var grace = Guid.NewGuid();

        var since = Base.AddMinutes(10);
        var until = Base.AddMinutes(20);

        // 1 minute before since — excluded.
        await SeedMessageAsync(
            (Address.AgentScheme, ada), (Address.AgentScheme, grace),
            since.AddMinutes(-1));
        // Exactly at since — included (inclusive lower bound).
        await SeedMessageAsync(
            (Address.AgentScheme, ada), (Address.AgentScheme, grace),
            since);
        // Exactly at until — excluded (exclusive upper bound).
        await SeedMessageAsync(
            (Address.AgentScheme, ada), (Address.AgentScheme, grace),
            until);

        var svc = BuildService();
        var history = await svc.GetHistoryAsync(new InteractionsHistoryFilters(
            Since: since, Until: until,
            Unit: null, Participant: null,
            Neighbours: 2, Cap: 50, MaxPulses: 5000),
            ct);

        history.Pulses.Count.ShouldBe(1);
        history.Pulses.Single().Timestamp.ShouldBe(since);
    }

    [Fact]
    public async Task GetHistoryAsync_DepthFilter_NeighboursAroundFocus()
    {
        // Topology: ada → grace, grace → hopper, hopper → lovelace.
        // Focus = grace; hops = 1 keeps the first two edges (ada↔grace,
        // grace↔hopper) and excludes hopper→lovelace.
        var ct = TestContext.Current.CancellationToken;
        var (ada, grace, hopper, lovelace) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        await SeedMessageAsync((Address.AgentScheme, ada), (Address.AgentScheme, grace), Base.AddMinutes(1));
        await SeedMessageAsync((Address.AgentScheme, grace), (Address.AgentScheme, hopper), Base.AddMinutes(2));
        await SeedMessageAsync((Address.AgentScheme, hopper), (Address.AgentScheme, lovelace), Base.AddMinutes(3));

        var svc = BuildService();
        var history = await svc.GetHistoryAsync(new InteractionsHistoryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: grace,
            Neighbours: 1, Cap: 50, MaxPulses: 5000),
            ct);

        history.Pulses.Count.ShouldBe(2);
        history.Pulses.Select(p => p.ToId).ShouldNotContain(GuidFormatter.Format(lovelace));
    }

    [Fact]
    public async Task GetHistoryAsync_PulseTimestampThreadIdChannelPopulated()
    {
        // Verify the per-pulse fields are populated from the message row
        // and the recipient scheme (the "channel" hint mirrors the
        // snapshot edge's Channels list).
        var ct = TestContext.Current.CancellationToken;
        var ada = Guid.NewGuid();
        var eng = Guid.NewGuid();
        var ts = Base.AddMinutes(7);

        await SeedMessageAsync((Address.AgentScheme, ada), (Address.UnitScheme, eng), ts);

        var svc = BuildService();
        var history = await svc.GetHistoryAsync(new InteractionsHistoryFilters(
            Since: Base, Until: Base.AddHours(1),
            Unit: null, Participant: null,
            Neighbours: 2, Cap: 50, MaxPulses: 5000),
            ct);

        var pulse = history.Pulses.Single();
        pulse.Timestamp.ShouldBe(ts);
        pulse.Channel.ShouldBe(Address.UnitScheme);
        pulse.ThreadId.ShouldNotBeNullOrEmpty();
    }

    private async Task SeedMessageAsync(
        (string Scheme, Guid Id) sender,
        (string Scheme, Guid Id) recipient,
        DateTimeOffset sentAt,
        string messageType = "Domain",
        Guid? tenantOverride = null,
        Guid? messageId = null)
    {
        // For a tenant-override seed we use the shared DbContext options
        // with a tenant-scoped context pointing at the override id, so
        // EF's tenant query filter is bypassed only via the constructor
        // ITenantContext — exactly the path the real OSS / cloud overlay
        // uses.
        var ctxTenant = tenantOverride is { } overrideId
            ? new SpringDbContext(_dbOptions, new StaticTenantContext(overrideId))
            : _db;

        var thread = new ThreadEntity
        {
            Id = Guid.NewGuid(),
            TenantId = tenantOverride ?? TenantId,
            ParticipantKey = $"{sender.Id:N}|{recipient.Id:N}",
            CreatedAt = sentAt,
            LastActivityAt = sentAt,
        };
        ctxTenant.Threads.Add(thread);

        ctxTenant.Messages.Add(new MessageEntity
        {
            Id = messageId ?? Guid.NewGuid(),
            TenantId = tenantOverride ?? TenantId,
            ThreadId = thread.Id,
            SenderScheme = sender.Scheme,
            SenderId = sender.Id,
            RecipientScheme = recipient.Scheme,
            RecipientId = recipient.Id,
            MessageType = messageType,
            Payload = "null",
            SentAt = sentAt,
        });
        await ctxTenant.SaveChangesAsync();
        if (tenantOverride is not null)
        {
            ctxTenant.Dispose();
        }
    }
}
