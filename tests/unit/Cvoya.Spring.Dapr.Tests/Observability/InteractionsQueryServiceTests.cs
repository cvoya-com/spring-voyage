// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Tenancy;
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
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    private InteractionsQueryService BuildService() => new(_db, _resolver);

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

    private async Task SeedMessageAsync(
        (string Scheme, Guid Id) sender,
        (string Scheme, Guid Id) recipient,
        DateTimeOffset sentAt,
        string messageType = "Domain",
        Guid? tenantOverride = null)
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
            Id = Guid.NewGuid(),
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
