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
/// Tests for <see cref="BoundaryFilteringExpertiseAggregator"/> — the
/// decorator over the raw expertise aggregator added by #413. Covers:
/// <list type="bullet">
///   <item><description>Opacity hides entries from outside callers.</description></item>
///   <item><description>Projection renames / retags / re-levels matching entries.</description></item>
///   <item><description>Caller-aware filtering: inside callers see raw, outside callers see boundary-applied.</description></item>
///   <item><description>Synthesis collapses raw entries into a unit-attributed aggregate.</description></item>
/// </list>
/// </summary>
public class BoundaryFilteringExpertiseAggregatorTests
{
    private readonly IExpertiseStore _store = Substitute.For<IExpertiseStore>();
    private readonly IDirectoryService _directory = Substitute.For<IDirectoryService>();
    private readonly IActorProxyFactory _proxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly IUnitMembershipRepository _memberships = Substitute.For<IUnitMembershipRepository>();
    private readonly IUnitBoundaryStore _boundaryStore = Substitute.For<IUnitBoundaryStore>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly Dictionary<string, IUnitActor> _unitActors = new();
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    public BoundaryFilteringExpertiseAggregatorTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        _store.GetDomainsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExpertiseDomain>());

        _directory.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var addr = ci.ArgAt<Address>(0);
                return new DirectoryEntry(addr, addr.Id, addr.Path, string.Empty, null, DateTimeOffset.UtcNow);
            });

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

        // Default: empty boundary everywhere. Per-test overrides replace this.
        _boundaryStore.GetAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(UnitBoundary.Empty);
    }

    private BoundaryFilteringExpertiseAggregator CreateAggregator()
    {
        var inner = new ExpertiseAggregator(
            _store, _directory, _proxyFactory, _memberships, _timeProvider, _loggerFactory);
        return new BoundaryFilteringExpertiseAggregator(
            inner, _boundaryStore, _timeProvider, _loggerFactory);
    }

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

    private void ArrangeBoundary(Address unit, UnitBoundary boundary)
    {
        _boundaryStore.GetAsync(
            Arg.Is<Address>(a => a == unit),
            Arg.Any<CancellationToken>()).Returns(boundary);
    }

    // ---------- Opacity ----------

    [Fact]
    public async Task GetAsync_Outside_OpacityRule_StripsMatchingEntries()
    {
        var aggregator = CreateAggregator();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var ada = Address.For("agent", TestSlugIds.HexFor("ada"));
        var kay = Address.For("agent", TestSlugIds.HexFor("kay"));

        RegisterUnit("eng", ada, kay);
        ArrangeExpertise(ada, new ExpertiseDomain("internal-secrets", "", ExpertiseLevel.Expert));
        ArrangeExpertise(kay, new ExpertiseDomain("react", "", ExpertiseLevel.Advanced));

        ArrangeBoundary(unit, new UnitBoundary(
            Opacities: new[] { new BoundaryOpacityRule(DomainPattern: "internal-*") }));

        var result = await aggregator.GetAsync(
            unit, BoundaryViewContext.External, TestContext.Current.CancellationToken);

        result.Entries.ShouldContain(e => e.Domain.Name == "react");
        result.Entries.ShouldNotContain(e => e.Domain.Name == "internal-secrets");
    }

    [Fact]
    public async Task GetAsync_Inside_OpacityRule_IsBypassed()
    {
        var aggregator = CreateAggregator();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var ada = Address.For("agent", TestSlugIds.HexFor("ada"));

        RegisterUnit("eng", ada);
        ArrangeExpertise(ada, new ExpertiseDomain("internal-secrets", "", ExpertiseLevel.Expert));

        ArrangeBoundary(unit, new UnitBoundary(
            Opacities: new[] { new BoundaryOpacityRule(DomainPattern: "internal-*") }));

        var result = await aggregator.GetAsync(
            unit, BoundaryViewContext.InsideUnit, TestContext.Current.CancellationToken);

        // Inside the unit: raw view, no filtering applied.
        result.Entries.ShouldContain(e => e.Domain.Name == "internal-secrets");
    }

    [Fact]
    public async Task GetAsync_Outside_OpacityByOrigin_StripsMatchingContributor()
    {
        var aggregator = CreateAggregator();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var ada = Address.For("agent", TestSlugIds.HexFor("ada"));
        var kay = Address.For("agent", TestSlugIds.HexFor("kay"));

        RegisterUnit("eng", ada, kay);
        ArrangeExpertise(ada, new ExpertiseDomain("python", "", ExpertiseLevel.Advanced));
        ArrangeExpertise(kay, new ExpertiseDomain("python", "", ExpertiseLevel.Advanced));

        // Hide every entry from ada (by origin), but keep kay's visible.
        ArrangeBoundary(unit, new UnitBoundary(
            Opacities: new[] { new BoundaryOpacityRule(OriginPattern: $"agent://{TestSlugIds.HexFor("ada")}") }));

        var result = await aggregator.GetAsync(
            unit, BoundaryViewContext.External, TestContext.Current.CancellationToken);

        result.Entries.Count(e => e.Domain.Name == "python").ShouldBe(1);
        result.Entries.Single(e => e.Domain.Name == "python").Origin.ShouldBe(kay);
    }

    // ---------- Projection ----------

    [Fact]
    public async Task GetAsync_Outside_ProjectionRule_RenamesEntries()
    {
        var aggregator = CreateAggregator();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var ada = Address.For("agent", TestSlugIds.HexFor("ada"));

        RegisterUnit("eng", ada);
        ArrangeExpertise(ada, new ExpertiseDomain("python/fastapi", "internal name", ExpertiseLevel.Expert));

        ArrangeBoundary(unit, new UnitBoundary(
            Projections: new[]
            {
                new BoundaryProjectionRule(
                    DomainPattern: "python/fastapi",
                    RenameTo: "backend-apis",
                    Retag: "public-facing",
                    OverrideLevel: ExpertiseLevel.Advanced),
            }));

        var result = await aggregator.GetAsync(
            unit, BoundaryViewContext.External, TestContext.Current.CancellationToken);

        var entry = result.Entries.ShouldHaveSingleItem();
        entry.Domain.Name.ShouldBe("backend-apis");
        entry.Domain.Description.ShouldBe("public-facing");
        entry.Domain.Level.ShouldBe(ExpertiseLevel.Advanced);
        // Origin preserved — permission checks (#414) still see the true contributor.
        entry.Origin.ShouldBe(ada);
    }

    [Fact]
    public async Task GetAsync_Outside_ProjectionFirstMatchWins()
    {
        var aggregator = CreateAggregator();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var ada = Address.For("agent", TestSlugIds.HexFor("ada"));

        RegisterUnit("eng", ada);
        ArrangeExpertise(ada, new ExpertiseDomain("react", "", ExpertiseLevel.Advanced));

        ArrangeBoundary(unit, new UnitBoundary(
            Projections: new[]
            {
                new BoundaryProjectionRule(DomainPattern: "react", RenameTo: "first-rule"),
                new BoundaryProjectionRule(DomainPattern: "react", RenameTo: "second-rule"),
            }));

        var result = await aggregator.GetAsync(
            unit, BoundaryViewContext.External, TestContext.Current.CancellationToken);

        result.Entries.ShouldHaveSingleItem().Domain.Name.ShouldBe("first-rule");
    }

    // ---------- Opacity wins over projection ----------

    [Fact]
    public async Task GetAsync_Outside_OpacityTakesPrecedenceOverProjection()
    {
        var aggregator = CreateAggregator();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var ada = Address.For("agent", TestSlugIds.HexFor("ada"));

        RegisterUnit("eng", ada);
        ArrangeExpertise(ada, new ExpertiseDomain("secret-sauce", "", ExpertiseLevel.Expert));

        ArrangeBoundary(unit, new UnitBoundary(
            Opacities: new[] { new BoundaryOpacityRule(DomainPattern: "secret-sauce") },
            Projections: new[]
            {
                new BoundaryProjectionRule(DomainPattern: "secret-sauce", RenameTo: "sauce"),
            }));

        var result = await aggregator.GetAsync(
            unit, BoundaryViewContext.External, TestContext.Current.CancellationToken);

        // Opacity wins — entry is gone, not renamed.
        result.Entries.ShouldBeEmpty();
    }

    // ---------- Synthesis ----------

    [Fact]
    public async Task GetAsync_Outside_SynthesisCollapsesRawMembersIntoUnitAggregate()
    {
        var aggregator = CreateAggregator();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var a1 = Address.For("agent", TestSlugIds.HexFor("a1"));
        var a2 = Address.For("agent", TestSlugIds.HexFor("a2"));
        var a3 = Address.For("agent", TestSlugIds.HexFor("a3"));

        RegisterUnit("eng", a1, a2, a3);
        ArrangeExpertise(a1, new ExpertiseDomain("react", "", ExpertiseLevel.Advanced));
        ArrangeExpertise(a2, new ExpertiseDomain("react", "", ExpertiseLevel.Expert));
        ArrangeExpertise(a3, new ExpertiseDomain("react", "", ExpertiseLevel.Beginner));

        ArrangeBoundary(unit, new UnitBoundary(
            Syntheses: new[]
            {
                new BoundarySynthesisRule(
                    Name: "team-react",
                    DomainPattern: "react",
                    Description: "aggregate team expertise"),
            }));

        var result = await aggregator.GetAsync(
            unit, BoundaryViewContext.External, TestContext.Current.CancellationToken);

        // The three raw React entries are consumed; one synthesised entry
        // (attributed to the unit) replaces them.
        result.Entries.ShouldHaveSingleItem();
        var entry = result.Entries[0];
        entry.Domain.Name.ShouldBe("team-react");
        entry.Domain.Description.ShouldBe("aggregate team expertise");
        entry.Origin.ShouldBe(unit);
        // Strongest level observed wins when the rule does not specify one.
        entry.Domain.Level.ShouldBe(ExpertiseLevel.Expert);
    }

    [Fact]
    public async Task GetAsync_Outside_SynthesisWithNoMatches_IsDropped()
    {
        var aggregator = CreateAggregator();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var ada = Address.For("agent", TestSlugIds.HexFor("ada"));

        RegisterUnit("eng", ada);
        ArrangeExpertise(ada, new ExpertiseDomain("python", "", ExpertiseLevel.Expert));

        ArrangeBoundary(unit, new UnitBoundary(
            Syntheses: new[]
            {
                new BoundarySynthesisRule(
                    Name: "team-react",
                    DomainPattern: "react"),
            }));

        var result = await aggregator.GetAsync(
            unit, BoundaryViewContext.External, TestContext.Current.CancellationToken);

        // The synthesised capability is not fabricated when no member contributes.
        result.Entries.ShouldNotContain(e => e.Domain.Name == "team-react");
        result.Entries.ShouldContain(e => e.Domain.Name == "python");
    }

    [Fact]
    public async Task GetAsync_Outside_SynthesisExplicitLevelOverridesStrongestSeen()
    {
        var aggregator = CreateAggregator();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var a1 = Address.For("agent", TestSlugIds.HexFor("a1"));
        var a2 = Address.For("agent", TestSlugIds.HexFor("a2"));

        RegisterUnit("eng", a1, a2);
        ArrangeExpertise(a1, new ExpertiseDomain("react", "", ExpertiseLevel.Expert));
        ArrangeExpertise(a2, new ExpertiseDomain("react", "", ExpertiseLevel.Advanced));

        ArrangeBoundary(unit, new UnitBoundary(
            Syntheses: new[]
            {
                new BoundarySynthesisRule(
                    Name: "team-react",
                    DomainPattern: "react",
                    Level: ExpertiseLevel.Intermediate),
            }));

        var result = await aggregator.GetAsync(
            unit, BoundaryViewContext.External, TestContext.Current.CancellationToken);

        result.Entries.ShouldHaveSingleItem().Domain.Level.ShouldBe(ExpertiseLevel.Intermediate);
    }

    // ---------- Internal bypass ----------

    [Fact]
    public async Task GetAsync_Legacy_NoContext_ReturnsRawView()
    {
        var aggregator = CreateAggregator();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var ada = Address.For("agent", TestSlugIds.HexFor("ada"));

        RegisterUnit("eng", ada);
        ArrangeExpertise(ada, new ExpertiseDomain("internal-secrets", "", ExpertiseLevel.Expert));

        ArrangeBoundary(unit, new UnitBoundary(
            Opacities: new[] { new BoundaryOpacityRule(DomainPattern: "internal-*") }));

        // The legacy GetAsync(address) overload delegates straight to the
        // inner aggregator and never consults the boundary — kept as the
        // "raw" entry point for callers that already have an inside-the-unit
        // identity.
        var result = await aggregator.GetAsync(unit, TestContext.Current.CancellationToken);

        result.Entries.ShouldContain(e => e.Domain.Name == "internal-secrets");
    }

    [Fact]
    public async Task GetAsync_Outside_EmptyBoundary_PassesThrough()
    {
        var aggregator = CreateAggregator();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var ada = Address.For("agent", TestSlugIds.HexFor("ada"));

        RegisterUnit("eng", ada);
        ArrangeExpertise(ada, new ExpertiseDomain("python", "", ExpertiseLevel.Expert));

        // No boundary configured → transparent view for outside callers too.
        ArrangeBoundary(unit, UnitBoundary.Empty);

        var result = await aggregator.GetAsync(
            unit, BoundaryViewContext.External, TestContext.Current.CancellationToken);

        result.Entries.ShouldContain(e => e.Domain.Name == "python" && e.Origin == ada);
    }

    // ---------- Boundary-store failures are non-fatal ----------

    [Fact]
    public async Task GetAsync_Outside_BoundaryStoreThrows_FallsBackToTransparent()
    {
        var aggregator = CreateAggregator();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var ada = Address.For("agent", TestSlugIds.HexFor("ada"));

        RegisterUnit("eng", ada);
        ArrangeExpertise(ada, new ExpertiseDomain("python", "", ExpertiseLevel.Expert));

        _boundaryStore.GetAsync(
            Arg.Is<Address>(a => a == unit),
            Arg.Any<CancellationToken>())
            .Returns<Task<UnitBoundary>>(_ => throw new InvalidOperationException("store-down"));

        var result = await aggregator.GetAsync(
            unit, BoundaryViewContext.External, TestContext.Current.CancellationToken);

        // Degrade to transparent — the entry is still there.
        result.Entries.ShouldContain(e => e.Domain.Name == "python");
    }
}