// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Capabilities;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Capabilities;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="InMemoryExpertiseSearch"/> — the lexical / full-text
/// expertise-directory search shipped as part of #542. Covers ranking, the
/// typed-contract and owner filters, boundary scoping, and pagination.
/// </summary>
public class InMemoryExpertiseSearchTests
{
    private readonly IDirectoryService _directory = Substitute.For<IDirectoryService>();
    private readonly IExpertiseStore _store = Substitute.For<IExpertiseStore>();
    private readonly IExpertiseAggregator _aggregator = Substitute.For<IExpertiseAggregator>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();

    public InMemoryExpertiseSearchTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        _directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DirectoryEntry>());
        _store.GetDomainsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExpertiseDomain>());
        _aggregator
            .GetAsync(Arg.Any<Address>(), Arg.Any<BoundaryViewContext>(), Arg.Any<CancellationToken>())
            .Returns(ci => new AggregatedExpertise(
                ci.ArgAt<Address>(0),
                Array.Empty<ExpertiseEntry>(),
                0,
                DateTimeOffset.UtcNow));
    }

    private InMemoryExpertiseSearch CreateSearch() =>
        new(_directory, _store, _aggregator, _loggerFactory);

    private static DirectoryEntry Entry(string scheme, string slug, string displayName)
    {
        var id = Guid.NewGuid();
        return new(new Address(scheme, id), id, displayName, string.Empty, null, DateTimeOffset.UtcNow);
    }

    private static ExpertiseDomain Domain(string name, string? schemaJson = null, string description = "") =>
        new(name, description, ExpertiseLevel.Advanced, schemaJson);

    [Fact]
    public async Task SearchAsync_ExactSlugMatch_RanksAboveTextMatch()
    {
        var unit = Entry("unit", "eng", "Engineering");
        var agent = Entry("agent", "ada", "Ada");
        _directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { unit, agent });
        _store.GetDomainsAsync(agent.Address, Arg.Any<CancellationToken>())
            .Returns(new[] { Domain("python") });
        _store.GetDomainsAsync(unit.Address, Arg.Any<CancellationToken>())
            .Returns(new[] { Domain("python-refactoring", description: "Refactoring Python code") });

        var search = CreateSearch();
        var result = await search.SearchAsync(
            new ExpertiseSearchQuery(Text: "python", Context: BoundaryViewContext.InsideUnit),
            TestContext.Current.CancellationToken);

        result.TotalCount.ShouldBe(2);
        result.Hits.Count.ShouldBe(2);
        // Exact slug "python" beats the substring hit on "python-refactoring".
        result.Hits[0].Slug.ShouldBe("python");
        result.Hits[0].Score.ShouldBeGreaterThan(result.Hits[1].Score);
    }

    [Fact]
    public async Task SearchAsync_ExternalCaller_HidesAgentLevelDirectHits()
    {
        var unit = Entry("unit", "eng", "Engineering");
        var agent = Entry("agent", "ada", "Ada");
        _directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { unit, agent });
        _store.GetDomainsAsync(agent.Address, Arg.Any<CancellationToken>())
            .Returns(new[] { Domain("python") });
        _store.GetDomainsAsync(unit.Address, Arg.Any<CancellationToken>())
            .Returns(new[] { Domain("release-planning") });

        var search = CreateSearch();
        var result = await search.SearchAsync(
            new ExpertiseSearchQuery(Context: BoundaryViewContext.External),
            TestContext.Current.CancellationToken);

        result.Hits.Select(h => h.Slug).ShouldNotContain("python");
        result.Hits.Select(h => h.Slug).ShouldContain("release-planning");
    }

    [Fact]
    public async Task SearchAsync_TypedOnly_SkipsConsultativeEntries()
    {
        var unit = Entry("unit", "eng", "Engineering");
        _directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { unit });
        _store.GetDomainsAsync(unit.Address, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Domain("typed", schemaJson: "{\"type\":\"object\"}"),
                Domain("consultative", schemaJson: null),
            });

        var search = CreateSearch();
        var result = await search.SearchAsync(
            new ExpertiseSearchQuery(TypedOnly: true, Context: BoundaryViewContext.InsideUnit),
            TestContext.Current.CancellationToken);

        result.Hits.Select(h => h.Slug).ShouldBe(new[] { "typed" });
    }

    [Fact]
    public async Task SearchAsync_OwnerFilter_RestrictsToExactAddress()
    {
        var unit = Entry("unit", "eng", "Engineering");
        var agent = Entry("agent", "ada", "Ada");
        var other = Entry("agent", "grace", "Grace");
        _directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { unit, agent, other });
        _store.GetDomainsAsync(agent.Address, Arg.Any<CancellationToken>())
            .Returns(new[] { Domain("python") });
        _store.GetDomainsAsync(other.Address, Arg.Any<CancellationToken>())
            .Returns(new[] { Domain("cobol") });

        var search = CreateSearch();
        var result = await search.SearchAsync(
            new ExpertiseSearchQuery(
                Owner: agent.Address,
                Context: BoundaryViewContext.InsideUnit),
            TestContext.Current.CancellationToken);

        result.Hits.ShouldHaveSingleItem();
        result.Hits[0].Owner.ShouldBe(agent.Address);
        result.Hits[0].Slug.ShouldBe("python");
    }

    [Fact]
    public async Task SearchAsync_DomainFilter_MatchesBothNameAndSlug()
    {
        var unit = Entry("unit", "eng", "Engineering");
        _directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { unit });
        _store.GetDomainsAsync(unit.Address, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                Domain("python/fastapi"),
                Domain("rust"),
                Domain("go"),
            });

        var search = CreateSearch();
        var result = await search.SearchAsync(
            new ExpertiseSearchQuery(
                Domains: new[] { "python-fastapi", "rust" },
                Context: BoundaryViewContext.InsideUnit),
            TestContext.Current.CancellationToken);

        result.Hits.Select(h => h.Slug).OrderBy(s => s, StringComparer.Ordinal)
            .ShouldBe(new[] { "python-fastapi", "rust" });
    }

    [Fact]
    public async Task SearchAsync_Pagination_ClampsAndReturnsTotal()
    {
        var unit = Entry("unit", "eng", "Engineering");
        _directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { unit });
        _store.GetDomainsAsync(unit.Address, Arg.Any<CancellationToken>())
            .Returns(Enumerable.Range(0, 30).Select(i => Domain($"skill-{i:D2}")).ToArray());

        var search = CreateSearch();
        var page1 = await search.SearchAsync(
            new ExpertiseSearchQuery(Limit: 10, Offset: 0, Context: BoundaryViewContext.InsideUnit),
            TestContext.Current.CancellationToken);

        page1.TotalCount.ShouldBe(30);
        page1.Hits.Count.ShouldBe(10);
        page1.Limit.ShouldBe(10);
        page1.Offset.ShouldBe(0);

        var page2 = await search.SearchAsync(
            new ExpertiseSearchQuery(Limit: 10, Offset: 10, Context: BoundaryViewContext.InsideUnit),
            TestContext.Current.CancellationToken);

        page2.Hits.Count.ShouldBe(10);
        page2.Hits[0].Slug.ShouldNotBe(page1.Hits[0].Slug);
    }

    [Fact]
    public async Task SearchAsync_Limit_ClampsToMax()
    {
        var unit = Entry("unit", "eng", "Engineering");
        _directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { unit });

        var search = CreateSearch();
        var result = await search.SearchAsync(
            new ExpertiseSearchQuery(Limit: 10_000),
            TestContext.Current.CancellationToken);

        result.Limit.ShouldBe(ExpertiseSearchQuery.MaxLimit);
    }

    [Fact]
    public async Task SearchAsync_AggregatedCoverage_SurfacesAsSeparateHit()
    {
        var rootUnit = Entry("unit", "root", "Root");
        var childUnit = Entry("unit", "child", "Child");
        _directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { rootUnit, childUnit });
        _store.GetDomainsAsync(rootUnit.Address, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExpertiseDomain>());
        _store.GetDomainsAsync(childUnit.Address, Arg.Any<CancellationToken>())
            .Returns(new[] { Domain("release-planning") });

        // Root's aggregator view surfaces the child's expertise via a path
        // [root, child] — the search must pick it up as an aggregated-coverage hit.
        _aggregator
            .GetAsync(rootUnit.Address, Arg.Any<BoundaryViewContext>(), Arg.Any<CancellationToken>())
            .Returns(new AggregatedExpertise(
                rootUnit.Address,
                new[]
                {
                    new ExpertiseEntry(
                        Domain("release-planning"),
                        childUnit.Address,
                        new[] { rootUnit.Address, childUnit.Address }),
                },
                1,
                DateTimeOffset.UtcNow));

        var search = CreateSearch();
        var result = await search.SearchAsync(
            new ExpertiseSearchQuery(Text: "release", Context: BoundaryViewContext.External),
            TestContext.Current.CancellationToken);

        // Two distinct hits: one direct (child's own) and one aggregated
        // (surfaced through root). Both should be visible to an external
        // caller because the origin is a unit, not an agent.
        result.Hits.Count.ShouldBe(2);
        result.Hits.Select(h => h.AggregatingUnit).ShouldContain((Address?)rootUnit.Address);
    }

    [Fact]
    public async Task SearchAsync_DirectHit_ReportsEmptyAncestorChainAndProjectionPaths()
    {
        // #553: a direct hit (entry declared on the unit itself) must not
        // claim any projecting ancestors. We pin the "empty, not null"
        // shape so API callers can treat "no chain" and "empty chain"
        // identically.
        var unit = Entry("unit", "eng", "Engineering");
        _directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { unit });
        _store.GetDomainsAsync(unit.Address, Arg.Any<CancellationToken>())
            .Returns(new[] { Domain("python") });

        var search = CreateSearch();
        var result = await search.SearchAsync(
            new ExpertiseSearchQuery(Text: "python", Context: BoundaryViewContext.InsideUnit),
            TestContext.Current.CancellationToken);

        var hit = result.Hits.ShouldHaveSingleItem();
        hit.AggregatingUnit.ShouldBeNull();
        hit.AncestorChain.ShouldNotBeNull();
        hit.AncestorChain!.Count.ShouldBe(0);
        hit.ProjectionPaths.ShouldNotBeNull();
        hit.ProjectionPaths!.Count.ShouldBe(0);
    }

    [Fact]
    public async Task SearchAsync_OneLevelProjection_PopulatesChainAndSingleProjectionPath()
    {
        // #553: single-level projection — unit `root` aggregates unit
        // `child`'s own expertise. Chain should have exactly the
        // aggregating unit; projection paths should have one
        // `projection/{slug}` entry.
        var rootUnit = Entry("unit", "root", "Root");
        var childUnit = Entry("unit", "child", "Child");
        _directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { rootUnit, childUnit });
        _store.GetDomainsAsync(rootUnit.Address, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExpertiseDomain>());
        _store.GetDomainsAsync(childUnit.Address, Arg.Any<CancellationToken>())
            .Returns(new[] { Domain("release-planning") });

        _aggregator
            .GetAsync(rootUnit.Address, Arg.Any<BoundaryViewContext>(), Arg.Any<CancellationToken>())
            .Returns(new AggregatedExpertise(
                rootUnit.Address,
                new[]
                {
                    new ExpertiseEntry(
                        Domain("release-planning"),
                        childUnit.Address,
                        new[] { rootUnit.Address, childUnit.Address }),
                },
                1,
                DateTimeOffset.UtcNow));

        var search = CreateSearch();
        var result = await search.SearchAsync(
            new ExpertiseSearchQuery(Text: "release", Context: BoundaryViewContext.External),
            TestContext.Current.CancellationToken);

        // One direct hit (child's own) plus one aggregated hit (surfaced
        // through root). We only inspect the aggregated one here.
        var aggregatedHit = result.Hits.Single(h => h.AggregatingUnit is not null);
        aggregatedHit.AncestorChain.ShouldNotBeNull();
        aggregatedHit.AncestorChain!.Count.ShouldBe(1);
        aggregatedHit.AncestorChain[0].ShouldBe(rootUnit.Address);
        aggregatedHit.ProjectionPaths.ShouldNotBeNull();
        aggregatedHit.ProjectionPaths!.ShouldBe(new[] { "projection/release-planning" });
    }

    [Fact]
    public async Task SearchAsync_MultiLevelProjection_ChainOrderedBottomUp()
    {
        // #553: three-level nesting — root -> mid -> origin. The root's
        // aggregated view surfaces origin's expertise via the full path
        // [root, mid, origin]. AncestorChain must list mid (closest to
        // the origin) first and root (highest) last.
        var rootUnit = Entry("unit", "root", "Root");
        var midUnit = Entry("unit", "mid", "Mid");
        var originUnit = Entry("unit", "origin", "Origin");
        _directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { rootUnit, midUnit, originUnit });
        _store.GetDomainsAsync(rootUnit.Address, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExpertiseDomain>());
        _store.GetDomainsAsync(midUnit.Address, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ExpertiseDomain>());
        _store.GetDomainsAsync(originUnit.Address, Arg.Any<CancellationToken>())
            .Returns(new[] { Domain("translation") });

        _aggregator
            .GetAsync(rootUnit.Address, Arg.Any<BoundaryViewContext>(), Arg.Any<CancellationToken>())
            .Returns(new AggregatedExpertise(
                rootUnit.Address,
                new[]
                {
                    new ExpertiseEntry(
                        Domain("translation"),
                        originUnit.Address,
                        new[] { rootUnit.Address, midUnit.Address, originUnit.Address }),
                },
                2,
                DateTimeOffset.UtcNow));

        var search = CreateSearch();
        var result = await search.SearchAsync(
            new ExpertiseSearchQuery(Text: "translation", Context: BoundaryViewContext.External),
            TestContext.Current.CancellationToken);

        var hit = result.Hits.Single(h => h.AggregatingUnit is not null);
        hit.AncestorChain.ShouldNotBeNull();
        // [mid, root] — bottom-up.
        hit.AncestorChain!.ShouldBe(new[] { midUnit.Address, rootUnit.Address });
        // One projection path per ancestor.
        hit.ProjectionPaths.ShouldNotBeNull();
        hit.ProjectionPaths!.Count.ShouldBe(2);
        hit.ProjectionPaths!.ShouldAllBe(p => p == "projection/translation");
    }

    [Fact]
    public async Task SearchAsync_EmptyQuery_ReturnsDirectoryContents()
    {
        var unit = Entry("unit", "eng", "Engineering");
        _directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { unit });
        _store.GetDomainsAsync(unit.Address, Arg.Any<CancellationToken>())
            .Returns(new[] { Domain("python"), Domain("rust") });

        var search = CreateSearch();
        var result = await search.SearchAsync(
            new ExpertiseSearchQuery(Context: BoundaryViewContext.InsideUnit),
            TestContext.Current.CancellationToken);

        result.Hits.Count.ShouldBe(2);
        result.Hits.Select(h => h.Slug).OrderBy(s => s, StringComparer.Ordinal)
            .ShouldBe(new[] { "python", "rust" });
    }
}