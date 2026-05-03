// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Capabilities;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Capabilities;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="ExpertiseAggregator"/>: recursive composition,
/// cycle guard, depth cap, origin tracking, and cache invalidation. See
/// #412.
/// </summary>
public class ExpertiseAggregatorTests
{
    private readonly IExpertiseStore _store = Substitute.For<IExpertiseStore>();
    private readonly IDirectoryService _directory = Substitute.For<IDirectoryService>();
    private readonly IActorProxyFactory _proxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly IUnitMembershipRepository _memberships = Substitute.For<IUnitMembershipRepository>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly Dictionary<string, IUnitActor> _unitActors = new();
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    public ExpertiseAggregatorTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        // No expertise unless the test arranges otherwise.
        _store.GetDomainsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExpertiseDomain>());

        // Directory resolves by default — ActorId = path — so the real
        // aggregator flow has something to hand to the proxy factory. Per-
        // test overrides can replace this.
        _directory.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var addr = ci.ArgAt<Address>(0);
                return new DirectoryEntry(addr, addr.Id, addr.Path, string.Empty, null, DateTimeOffset.UtcNow);
            });

        // Proxy factory hands out per-path substitute IUnitActor instances
        // the tests configure through RegisterUnit().
        _proxyFactory.CreateActorProxy<IUnitActor>(Arg.Any<ActorId>(), nameof(UnitActor))
            .Returns(ci =>
            {
                var actorId = ci.ArgAt<ActorId>(0).GetId();
                if (!_unitActors.TryGetValue(actorId, out var actor))
                {
                    actor = Substitute.For<IUnitActor>();
                    actor.GetMembersAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<Address>());
                    _unitActors[actorId] = actor;
                }
                return actor;
            });

        _memberships.ListByAgentAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UnitMembership>());
        _directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DirectoryEntry>());
    }

    private ExpertiseAggregator CreateAggregator() =>
        new(_store, _directory, _proxyFactory, _memberships, _timeProvider, _loggerFactory);

    private void RegisterUnit(string unitId, params Address[] members)
    {
        // Production code creates actor proxies with ActorId = the unit's
        // Guid hex (post-#1629). Tests pass slug-shaped names; map them to
        // the same Guid hex used by Address.For so proxy lookups hit.
        var key = TestSlugIds.HexFor(unitId);
        if (!_unitActors.TryGetValue(key, out var actor))
        {
            actor = Substitute.For<IUnitActor>();
            _unitActors[key] = actor;
        }
        actor.GetMembersAsync(Arg.Any<CancellationToken>()).Returns(members);
    }

    private void ArrangeExpertise(Address address, params ExpertiseDomain[] domains)
    {
        _store.GetDomainsAsync(address, Arg.Any<CancellationToken>()).Returns(domains);
    }

    [Fact]
    public async Task GetAsync_EmptyUnit_ReturnsEmptyAggregation()
    {
        var aggregator = CreateAggregator();
        var unit = Address.For("unit", TestSlugIds.HexFor("empty"));
        RegisterUnit("empty");

        var result = await aggregator.GetAsync(unit, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.Unit.ShouldBe(unit);
        result.Entries.ShouldBeEmpty();
        result.Depth.ShouldBe(0);
    }

    [Fact]
    public async Task GetAsync_FlatUnitWithAgents_IncludesAgentExpertiseWithOrigin()
    {
        var aggregator = CreateAggregator();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var ada = Address.For("agent", TestSlugIds.HexFor("ada"));
        var kay = Address.For("agent", TestSlugIds.HexFor("kay"));

        RegisterUnit("eng", ada, kay);
        ArrangeExpertise(ada, new ExpertiseDomain("python", "FastAPI", ExpertiseLevel.Expert));
        ArrangeExpertise(kay, new ExpertiseDomain("react", "Next.js", ExpertiseLevel.Advanced));

        var result = await aggregator.GetAsync(unit, TestContext.Current.CancellationToken);

        result.Entries.Count.ShouldBe(2);
        result.Entries.ShouldContain(e => e.Domain.Name == "python" && e.Origin == ada);
        result.Entries.ShouldContain(e => e.Domain.Name == "react" && e.Origin == kay);
        result.Depth.ShouldBe(1);

        // Each entry's Path should be [unit, origin].
        var python = result.Entries.Single(e => e.Domain.Name == "python");
        python.Path.ShouldBe(new[] { unit, ada });
    }

    [Fact]
    public async Task GetAsync_ThreeLevelNesting_ComposesRecursivelyToLeaves()
    {
        var aggregator = CreateAggregator();
        // root -> eng -> backend -> ada
        var root = Address.For("unit", TestSlugIds.HexFor("root"));
        var eng = Address.For("unit", TestSlugIds.HexFor("eng"));
        var backend = Address.For("unit", TestSlugIds.HexFor("backend"));
        var ada = Address.For("agent", TestSlugIds.HexFor("ada"));
        var dijkstra = Address.For("agent", TestSlugIds.HexFor("dijkstra"));

        RegisterUnit("root", eng);
        RegisterUnit("eng", backend);
        RegisterUnit("backend", ada, dijkstra);

        ArrangeExpertise(ada, new ExpertiseDomain("csharp", "server-side", ExpertiseLevel.Expert));
        ArrangeExpertise(dijkstra, new ExpertiseDomain("algorithms", "graph theory", ExpertiseLevel.Expert));

        var result = await aggregator.GetAsync(root, TestContext.Current.CancellationToken);

        result.Entries.Count.ShouldBe(2);
        result.Depth.ShouldBe(3);
        var csharp = result.Entries.Single(e => e.Domain.Name == "csharp");
        csharp.Origin.ShouldBe(ada);
        csharp.Path.ShouldBe(new[] { root, eng, backend, ada });
    }

    [Fact]
    public async Task GetAsync_UnitOwnExpertiseIncluded()
    {
        var aggregator = CreateAggregator();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        RegisterUnit("eng");

        ArrangeExpertise(unit, new ExpertiseDomain("full-stack", "synthesized", ExpertiseLevel.Advanced));

        var result = await aggregator.GetAsync(unit, TestContext.Current.CancellationToken);

        result.Entries.Count.ShouldBe(1);
        result.Entries[0].Origin.ShouldBe(unit);
        result.Entries[0].Path.ShouldBe(new[] { unit });
    }

    [Fact]
    public async Task GetAsync_DuplicateDomainAcrossPaths_StrongerLevelWins()
    {
        var aggregator = CreateAggregator();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var ada = Address.For("agent", TestSlugIds.HexFor("ada"));

        RegisterUnit("eng", ada);
        // Arrange two reads for same (domain, origin) with different levels —
        // simulates two passes with different writes. In practice de-dup
        // happens across origins; here we supply two entries to the store in
        // a single call for the same agent, and expect the stronger one.
        ArrangeExpertise(ada,
            new ExpertiseDomain("python", "intro", ExpertiseLevel.Beginner),
            new ExpertiseDomain("python", "expert-level", ExpertiseLevel.Expert));

        var result = await aggregator.GetAsync(unit, TestContext.Current.CancellationToken);

        var python = result.Entries.Single();
        python.Domain.Level.ShouldBe(ExpertiseLevel.Expert);
    }

    [Fact]
    public async Task GetAsync_DirectCycleThrows()
    {
        var aggregator = CreateAggregator();
        // root is a member of itself — the membership-time cycle check in
        // UnitActor.AddMemberAsync should have rejected this, but if state
        // is corrupted the aggregator must refuse to loop.
        var root = Address.For("unit", TestSlugIds.HexFor("root"));
        RegisterUnit("root", root);

        await Should.ThrowAsync<ExpertiseAggregationException>(() =>
            aggregator.GetAsync(root, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetAsync_TwoCycle_Throws()
    {
        var aggregator = CreateAggregator();
        var a = Address.For("unit", TestSlugIds.HexFor("a"));
        var b = Address.For("unit", TestSlugIds.HexFor("b"));

        // a contains b, b contains a.
        RegisterUnit("a", b);
        RegisterUnit("b", a);

        var ex = await Should.ThrowAsync<ExpertiseAggregationException>(() =>
            aggregator.GetAsync(a, TestContext.Current.CancellationToken));
        ex.Path.Count.ShouldBeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task GetAsync_BenignDAGConvergence_DoesNotThrow()
    {
        var aggregator = CreateAggregator();
        // root -> [a, b] -> both contain shared. `shared` is visited twice
        // via different paths but isn't a cycle reaching back to root.
        var root = Address.For("unit", TestSlugIds.HexFor("root"));
        var a = Address.For("unit", TestSlugIds.HexFor("a"));
        var b = Address.For("unit", TestSlugIds.HexFor("b"));
        var shared = Address.For("unit", TestSlugIds.HexFor("shared"));
        var leafAgent = Address.For("agent", TestSlugIds.HexFor("leaf"));

        RegisterUnit("root", a, b);
        RegisterUnit("a", shared);
        RegisterUnit("b", shared);
        RegisterUnit("shared", leafAgent);
        ArrangeExpertise(leafAgent, new ExpertiseDomain("leaf-skill", "", ExpertiseLevel.Advanced));

        var result = await aggregator.GetAsync(root, TestContext.Current.CancellationToken);

        // The shared leaf's expertise is included exactly once (dedup by
        // (domain, origin)).
        result.Entries.Count(e => e.Domain.Name == "leaf-skill").ShouldBe(1);
    }

    [Fact]
    public async Task GetAsync_CachesResult_SecondCallSkipsRecompute()
    {
        var aggregator = CreateAggregator();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var ada = Address.For("agent", TestSlugIds.HexFor("ada"));
        RegisterUnit("eng", ada);
        ArrangeExpertise(ada, new ExpertiseDomain("python", "", ExpertiseLevel.Advanced));

        await aggregator.GetAsync(unit, TestContext.Current.CancellationToken);
        await aggregator.GetAsync(unit, TestContext.Current.CancellationToken);

        // Each GetAsync on the unit reads the store once for `unit` and once
        // for each member. With cache, the second call should not re-read
        // anything.
        await _store.Received(1).GetDomainsAsync(ada, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidateAsync_ForUnit_EvictsItFromCache()
    {
        var aggregator = CreateAggregator();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var ada = Address.For("agent", TestSlugIds.HexFor("ada"));
        RegisterUnit("eng", ada);
        ArrangeExpertise(ada, new ExpertiseDomain("python", "", ExpertiseLevel.Advanced));

        await aggregator.GetAsync(unit, TestContext.Current.CancellationToken);
        await aggregator.InvalidateAsync(unit, TestContext.Current.CancellationToken);
        await aggregator.GetAsync(unit, TestContext.Current.CancellationToken);

        await _store.Received(2).GetDomainsAsync(ada, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidateAsync_ForAgent_EvictsEveryUnitThatContainsIt()
    {
        // Under #1629 the aggregator looks up an agent's memberships through
        // the directory's ActorId; the directory entry's ActorId must equal
        // the address Guid so the eviction loop walks back to the same unit
        // cache entry the GetAsync() populated.
        var adaUuid = TestSlugIds.For("ada");
        var engUuid = TestSlugIds.For("eng");

        var aggregator = CreateAggregator();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var ada = Address.For("agent", TestSlugIds.HexFor("ada"));

        RegisterUnit("eng", ada);
        ArrangeExpertise(ada, new ExpertiseDomain("python", "", ExpertiseLevel.Advanced));

        // Directory resolves "ada" to a stable UUID so the aggregator can look up memberships.
        _directory.ResolveAsync(ada, Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(ada, adaUuid, "ada", string.Empty, null, DateTimeOffset.UtcNow));

        // ListAllAsync must include the "eng" unit with its UUID for the reverse walk.
        _directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new DirectoryEntry(unit, engUuid, "eng", string.Empty, null, DateTimeOffset.UtcNow),
            });

        _memberships.ListByAgentAsync(adaUuid, Arg.Any<CancellationToken>())
            .Returns(new[] { new UnitMembership(engUuid, adaUuid) });

        await aggregator.GetAsync(unit, TestContext.Current.CancellationToken);
        // Invalidation driven by an agent-level edit must evict the unit's
        // cached aggregation.
        await aggregator.InvalidateAsync(ada, TestContext.Current.CancellationToken);
        await aggregator.GetAsync(unit, TestContext.Current.CancellationToken);

        await _store.Received(2).GetDomainsAsync(ada, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidateAsync_MidTreeChange_PropagatesToAncestorCaches()
    {
        // root -> mid -> leaf   (agent under leaf)
        var aggregator = CreateAggregator();
        var root = Address.For("unit", TestSlugIds.HexFor("root"));
        var mid = Address.For("unit", TestSlugIds.HexFor("mid"));
        var leaf = Address.For("unit", TestSlugIds.HexFor("leaf"));
        var ada = Address.For("agent", TestSlugIds.HexFor("ada"));

        RegisterUnit("root", mid);
        RegisterUnit("mid", leaf);
        RegisterUnit("leaf", ada);
        ArrangeExpertise(ada, new ExpertiseDomain("python", "", ExpertiseLevel.Advanced));

        // The directory must list every unit for the reverse-parent walk.
        _directory.ListAllAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new DirectoryEntry(root, root.Id, "root", string.Empty, null, DateTimeOffset.UtcNow),
            new DirectoryEntry(mid, mid.Id, "mid", string.Empty, null, DateTimeOffset.UtcNow),
            new DirectoryEntry(leaf, leaf.Id, "leaf", string.Empty, null, DateTimeOffset.UtcNow),
        });

        // Warm the cache at root.
        await aggregator.GetAsync(root, TestContext.Current.CancellationToken);

        // A mid-tree change (leaf's expertise flipped) invalidates leaf + its
        // ancestors (mid + root). The next root read must recompute.
        await aggregator.InvalidateAsync(leaf, TestContext.Current.CancellationToken);
        await aggregator.GetAsync(root, TestContext.Current.CancellationToken);

        await _store.Received(2).GetDomainsAsync(ada, Arg.Any<CancellationToken>());
    }
}