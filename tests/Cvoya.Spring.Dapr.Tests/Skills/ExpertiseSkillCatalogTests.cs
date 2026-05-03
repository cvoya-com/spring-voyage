// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Skills;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Skills;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="ExpertiseSkillCatalog"/> — the live expertise-directory-
/// driven skill enumeration shipped as part of the #359 rework. Covers:
/// <list type="bullet">
///   <item><description>Live enumeration (no snapshot — a fresh expertise entry
///     is visible on the next call).</description></item>
///   <item><description>Typed-contract eligibility (consultative-only entries
///     are excluded).</description></item>
///   <item><description>Boundary filter (agent-level expertise hidden from
///     outside callers when not unit-projected).</description></item>
///   <item><description>Name scheme (<c>expertise/{slug}</c>, no agent name in
///     the skill name).</description></item>
/// </list>
/// </summary>
public class ExpertiseSkillCatalogTests
{
    private readonly IDirectoryService _directory = Substitute.For<IDirectoryService>();
    private readonly IExpertiseAggregator _aggregator = Substitute.For<IExpertiseAggregator>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();

    public ExpertiseSkillCatalogTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        // Default: one unit ("eng"), empty expertise. Per-test overrides
        // replace this. The unit's identity is anchored to TestSlugIds.For("eng")
        // so test-fixture addresses (Address.For("unit", TestSlugIds.HexFor("eng")))
        // line up with the directory entry the catalog walks.
        var engId = TestSlugIds.For("eng");
        _directory.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new DirectoryEntry(
                    new Address("unit", engId),
                    engId, "Engineering", string.Empty, null, DateTimeOffset.UtcNow),
            });

        _aggregator
            .GetAsync(Arg.Any<Address>(), Arg.Any<BoundaryViewContext>(), Arg.Any<CancellationToken>())
            .Returns(ci => new AggregatedExpertise(
                ci.ArgAt<Address>(0),
                Array.Empty<ExpertiseEntry>(),
                0,
                DateTimeOffset.UtcNow));
    }

    private ExpertiseSkillCatalog CreateCatalog() =>
        new(_directory, _aggregator, _loggerFactory);

    private static ExpertiseDomain TypedDomain(string name, string? schemaJson = null) =>
        new(name, $"{name} description", ExpertiseLevel.Advanced, schemaJson ?? "{\"type\":\"object\"}");

    [Fact]
    public async Task EnumerateAsync_NoTypedContract_SkipsConsultativeEntries()
    {
        var catalog = CreateCatalog();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var entries = new[]
        {
            // Typed contract — should surface.
            new ExpertiseEntry(
                TypedDomain("python"),
                unit,
                new[] { unit }),
            // Consultative-only (InputSchemaJson = null) — should NOT surface.
            new ExpertiseEntry(
                new ExpertiseDomain("advice", "general advice", ExpertiseLevel.Expert, InputSchemaJson: null),
                unit,
                new[] { unit }),
        };
        _aggregator
            .GetAsync(unit, Arg.Any<BoundaryViewContext>(), Arg.Any<CancellationToken>())
            .Returns(new AggregatedExpertise(unit, entries, 0, DateTimeOffset.UtcNow));

        var skills = await catalog.EnumerateAsync(BoundaryViewContext.InsideUnit, TestContext.Current.CancellationToken);

        skills.ShouldHaveSingleItem();
        skills[0].SkillName.ShouldBe("expertise/python");
    }

    [Fact]
    public async Task EnumerateAsync_ExternalCaller_HidesAgentLevelExpertiseThatIsNotUnitProjected()
    {
        var catalog = CreateCatalog();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var agent = Address.For("agent", TestSlugIds.HexFor("ada"));

        // Outside caller: only unit-projected entries should be visible.
        _aggregator
            .GetAsync(unit, Arg.Is<BoundaryViewContext>(c => !c.Internal), Arg.Any<CancellationToken>())
            .Returns(new AggregatedExpertise(unit, new[]
            {
                // Unit-projected (origin = unit) — eligible.
                new ExpertiseEntry(TypedDomain("release-planning"), unit, new[] { unit }),
                // Agent-level expertise still in the filtered view (a
                // misconfigured boundary would let it through) — this test
                // asserts the catalog itself hides it as defence in depth.
                new ExpertiseEntry(TypedDomain("python"), agent, new[] { unit, agent }),
            }, 1, DateTimeOffset.UtcNow));

        var skills = await catalog.EnumerateAsync(BoundaryViewContext.External, TestContext.Current.CancellationToken);

        skills.Select(s => s.SkillName).ShouldBe(new[] { "expertise/release-planning" });
        skills[0].Target.ShouldBe(unit);
    }

    [Fact]
    public async Task EnumerateAsync_InsideCaller_SeesAgentLevelExpertise()
    {
        var catalog = CreateCatalog();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var agent = Address.For("agent", TestSlugIds.HexFor("ada"));

        _aggregator
            .GetAsync(unit, Arg.Is<BoundaryViewContext>(c => c.Internal), Arg.Any<CancellationToken>())
            .Returns(new AggregatedExpertise(unit, new[]
            {
                new ExpertiseEntry(TypedDomain("python"), agent, new[] { unit, agent }),
            }, 1, DateTimeOffset.UtcNow));

        var skills = await catalog.EnumerateAsync(BoundaryViewContext.InsideUnit, TestContext.Current.CancellationToken);

        skills.ShouldHaveSingleItem();
        skills[0].SkillName.ShouldBe("expertise/python");
        skills[0].Target.ShouldBe(agent);
    }

    [Fact]
    public async Task EnumerateAsync_AgentNameNeverAppearsInSkillName()
    {
        var catalog = CreateCatalog();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var agent = Address.For("agent", TestSlugIds.HexFor("ada-the-senior-engineer"));
        _aggregator
            .GetAsync(unit, Arg.Any<BoundaryViewContext>(), Arg.Any<CancellationToken>())
            .Returns(new AggregatedExpertise(unit, new[]
            {
                new ExpertiseEntry(TypedDomain("python/fastapi"), agent, new[] { unit, agent }),
            }, 1, DateTimeOffset.UtcNow));

        var skills = await catalog.EnumerateAsync(BoundaryViewContext.InsideUnit, TestContext.Current.CancellationToken);

        skills.ShouldHaveSingleItem();
        // Agent name is absent; directory-keyed slug wins.
        skills[0].SkillName.ShouldBe("expertise/python-fastapi");
        skills[0].SkillName.ShouldNotContain("ada");
    }

    [Fact]
    public async Task EnumerateAsync_LiveResolution_NewExpertisePropagatesWithoutRestart()
    {
        var catalog = CreateCatalog();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));

        // First call: no expertise at all.
        _aggregator
            .GetAsync(unit, Arg.Any<BoundaryViewContext>(), Arg.Any<CancellationToken>())
            .Returns(new AggregatedExpertise(unit, Array.Empty<ExpertiseEntry>(), 0, DateTimeOffset.UtcNow));

        var before = await catalog.EnumerateAsync(BoundaryViewContext.InsideUnit, TestContext.Current.CancellationToken);
        before.ShouldBeEmpty();

        // Arrange: a fresh expertise entry is now visible to the aggregator —
        // simulating a mutation (agent gains expertise / unit projection).
        _aggregator
            .GetAsync(unit, Arg.Any<BoundaryViewContext>(), Arg.Any<CancellationToken>())
            .Returns(new AggregatedExpertise(unit, new[]
            {
                new ExpertiseEntry(TypedDomain("python"), unit, new[] { unit }),
            }, 0, DateTimeOffset.UtcNow));

        // No restart — second enumeration picks it up.
        var after = await catalog.EnumerateAsync(BoundaryViewContext.InsideUnit, TestContext.Current.CancellationToken);

        after.ShouldHaveSingleItem();
        after[0].SkillName.ShouldBe("expertise/python");
    }

    [Fact]
    public async Task ResolveAsync_HiddenFromCaller_ReturnsNull()
    {
        var catalog = CreateCatalog();
        var unit = Address.For("unit", TestSlugIds.HexFor("eng"));
        var agent = Address.For("agent", TestSlugIds.HexFor("ada"));

        // Boundary is caller-aware: external caller sees nothing; internal sees the agent entry.
        _aggregator
            .GetAsync(unit, Arg.Is<BoundaryViewContext>(c => !c.Internal), Arg.Any<CancellationToken>())
            .Returns(new AggregatedExpertise(unit, Array.Empty<ExpertiseEntry>(), 0, DateTimeOffset.UtcNow));
        _aggregator
            .GetAsync(unit, Arg.Is<BoundaryViewContext>(c => c.Internal), Arg.Any<CancellationToken>())
            .Returns(new AggregatedExpertise(unit, new[]
            {
                new ExpertiseEntry(TypedDomain("python"), agent, new[] { unit, agent }),
            }, 1, DateTimeOffset.UtcNow));

        var external = await catalog.ResolveAsync("expertise/python", BoundaryViewContext.External, TestContext.Current.CancellationToken);
        external.ShouldBeNull();

        var inside = await catalog.ResolveAsync("expertise/python", BoundaryViewContext.InsideUnit, TestContext.Current.CancellationToken);
        inside.ShouldNotBeNull();
        inside.Target.ShouldBe(agent);
    }
}