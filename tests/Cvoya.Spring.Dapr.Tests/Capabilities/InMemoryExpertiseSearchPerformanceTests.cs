// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Capabilities;

using System.Diagnostics;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Capabilities;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Performance validation for <see cref="InMemoryExpertiseSearch"/> against
/// the acceptance bar in issue #542 — a search on a tenant with 1000
/// expertise entries must return in under 200ms. The test seeds a synthetic
/// directory of 1000 agents, each contributing one domain, and runs the
/// query against the in-memory default. A CI flake buffer is baked in
/// (<see cref="PerfBudgetMs"/>) so the test fails only when we regress past
/// the acceptance bar plus reasonable headroom on a slow runner.
/// </summary>
public class InMemoryExpertiseSearchPerformanceTests
{
    /// <summary>
    /// Performance budget. Issue #542 sets the bar at 200ms; we give a
    /// small margin so a single noisy CI run doesn't flake us. If this
    /// fails, the fix is to profile the search (likely the
    /// directory ListAllAsync or per-entity store fan-out) rather than
    /// bump the budget further — the in-memory path should be well
    /// inside the bar on modern hardware.
    /// </summary>
    private const int PerfBudgetMs = 400;

    private readonly IDirectoryService _directory = Substitute.For<IDirectoryService>();
    private readonly IExpertiseStore _store = Substitute.For<IExpertiseStore>();
    private readonly IExpertiseAggregator _aggregator = Substitute.For<IExpertiseAggregator>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();

    public InMemoryExpertiseSearchPerformanceTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        _aggregator
            .GetAsync(Arg.Any<Address>(), Arg.Any<BoundaryViewContext>(), Arg.Any<CancellationToken>())
            .Returns(ci => new AggregatedExpertise(
                ci.ArgAt<Address>(0),
                Array.Empty<ExpertiseEntry>(),
                0,
                DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task SearchAsync_OneThousandEntries_ReturnsWithinPerfBudget()
    {
        // Arrange: 1000 agents, each with one unique domain.
        var entries = new DirectoryEntry[1000];
        var domainMap = new Dictionary<Address, IReadOnlyList<ExpertiseDomain>>();
        for (var i = 0; i < entries.Length; i++)
        {
            var address = new Address("agent", $"agent-{i:D4}");
            entries[i] = new DirectoryEntry(
                address,
                address.Path,
                $"Agent {i}",
                string.Empty,
                null,
                DateTimeOffset.UtcNow);

            // Mix typed/consultative and spread some "python" hits so the
            // text query has something to rank.
            var name = i % 13 == 0 ? $"python-module-{i}" : $"skill-{i:D4}";
            var typed = i % 2 == 0 ? "{\"type\":\"object\"}" : null;
            domainMap[address] = new[]
            {
                new ExpertiseDomain(name, $"{name} description for entry {i}", ExpertiseLevel.Advanced, typed),
            };
        }

        _directory.ListAllAsync(Arg.Any<CancellationToken>()).Returns(entries);
        _store.GetDomainsAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var addr = ci.ArgAt<Address>(0);
                return domainMap.TryGetValue(addr, out var d)
                    ? d
                    : Array.Empty<ExpertiseDomain>();
            });

        var search = new InMemoryExpertiseSearch(_directory, _store, _aggregator, _loggerFactory);

        // Warm up — the substitute's first dispatch pays a JIT / proxy cost
        // that would otherwise inflate the measured call.
        _ = await search.SearchAsync(
            new ExpertiseSearchQuery(Text: "warmup", Context: BoundaryViewContext.InsideUnit),
            TestContext.Current.CancellationToken);

        // Act.
        var stopwatch = Stopwatch.StartNew();
        var result = await search.SearchAsync(
            new ExpertiseSearchQuery(Text: "python", Context: BoundaryViewContext.InsideUnit),
            TestContext.Current.CancellationToken);
        stopwatch.Stop();

        // Assert: within the budget, and the python-matching entries surfaced.
        stopwatch.ElapsedMilliseconds.ShouldBeLessThan(
            PerfBudgetMs,
            $"Search took {stopwatch.ElapsedMilliseconds}ms — acceptance bar is 200ms (budget includes CI headroom).");
        result.TotalCount.ShouldBeGreaterThan(0);
        result.Hits[0].Slug.ShouldContain("python");
    }
}