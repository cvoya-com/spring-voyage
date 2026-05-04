// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Observability;

using Microsoft.EntityFrameworkCore;

using Shouldly;

using Xunit;

/// <summary>
/// Integration tests for <see cref="AnalyticsQueryService.GetWaitTimesAsync"/>
/// — closes the acceptance criteria on #476 (duration fields populated from
/// the Rx activity pipeline). Tests seed a known sequence of
/// <c>StateChanged</c> events in the in-memory EF context and assert that
/// paired lifecycle transitions compute the expected idle / busy /
/// waiting-for-human durations. Non-canonical metadata-edit events count
/// toward <see cref="Core.Observability.WaitTimeEntry.StateTransitions"/> but
/// not toward the duration buckets.
/// </summary>
public class AnalyticsQueryServiceTests : IDisposable
{
    private readonly SpringDbContext _db;

    public AnalyticsQueryServiceTests()
    {
        var dbOptions = new DbContextOptionsBuilder<SpringDbContext>()
            .UseInMemoryDatabase($"AnalyticsQueryTest-{Guid.NewGuid()}")
            .Options;
        _db = new SpringDbContext(dbOptions);
    }

    public void Dispose()
    {
        _db.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task GetWaitTimesAsync_NoEvents_ReturnsEmptyRollup()
    {
        var svc = new AnalyticsQueryService(_db);

        var result = await svc.GetWaitTimesAsync(
            sourceFilter: null,
            from: DateTimeOffset.UtcNow.AddHours(-1),
            to: DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);

        result.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task GetWaitTimesAsync_MultipleTransitions_AccumulatesPerBucket()
    {
        // Timeline for agent://ada (single source, covers all three buckets):
        //   t=0s    Idle    → Active      (busy starts)
        //   t=60s   Active  → Paused      (busy: 60s; waiting-for-human starts)
        //   t=90s   Paused  → Active      (waiting-for-human: 30s; busy resumes)
        //   t=120s  Active  → Idle        (busy: +30s → 90s total; idle starts)
        //   t=150s  window end             (idle: 30s)
        var baseTime = DateTimeOffset.Parse("2026-04-10T12:00:00Z");
        var windowEnd = baseTime.AddSeconds(150);

        await SeedLifecycleAsync("agent:ada", new[]
        {
            (baseTime.AddSeconds(0),   "Idle",   "Active"),
            (baseTime.AddSeconds(60),  "Active", "Paused"),
            (baseTime.AddSeconds(90),  "Paused", "Active"),
            (baseTime.AddSeconds(120), "Active", "Idle"),
        });

        var svc = new AnalyticsQueryService(_db);

        var result = await svc.GetWaitTimesAsync(
            sourceFilter: null,
            from: baseTime.AddSeconds(-1),
            to: windowEnd,
            TestContext.Current.CancellationToken);

        result.Entries.Count.ShouldBe(1);
        var entry = result.Entries[0];
        entry.Source.ShouldBe(SourceHex("agent:ada"));
        entry.BusySeconds.ShouldBe(90);            // 0→60 + 90→120
        entry.WaitingForHumanSeconds.ShouldBe(30); // 60→90
        entry.IdleSeconds.ShouldBe(30);            // 120→150 (clamped to windowEnd)
        entry.StateTransitions.ShouldBe(4);
    }

    [Fact]
    public async Task GetWaitTimesAsync_MetadataEditEvents_CountedButDontAttributeDuration()
    {
        // Metadata-edit StateChanged events carry an `action` payload rather
        // than `{from, to}`. They should count toward StateTransitions but
        // not create spurious duration spans. Timeline:
        //   t=0s   Idle → Active (canonical, starts busy span)
        //   t=30s  metadata edit (non-canonical; doesn't close busy span)
        //   t=60s  Active → Idle (canonical, closes busy at 60s total)
        var baseTime = DateTimeOffset.Parse("2026-04-10T12:00:00Z");
        var windowEnd = baseTime.AddSeconds(60);

        _db.ActivityEvents.Add(BuildLifecycle("agent:ada", baseTime.AddSeconds(0), "Idle", "Active"));
        _db.ActivityEvents.Add(new ActivityEventRecord
        {
            Id = Guid.NewGuid(),
            SourceId = SourceGuid("agent:ada"),
            EventType = nameof(ActivityEventType.StateChanged),
            Severity = "Info",
            Summary = "Agent metadata updated: Model",
            Timestamp = baseTime.AddSeconds(30),
            Details = JsonSerializer.SerializeToElement(new
            {
                action = "AgentMetadataUpdated",
                fields = new[] { "Model" },
            }),
        });
        _db.ActivityEvents.Add(BuildLifecycle("agent:ada", baseTime.AddSeconds(60), "Active", "Idle"));
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var svc = new AnalyticsQueryService(_db);

        var result = await svc.GetWaitTimesAsync(
            sourceFilter: null,
            from: baseTime.AddSeconds(-1),
            to: windowEnd,
            TestContext.Current.CancellationToken);

        var entry = result.Entries.Single();
        // 0 → 60 is a single busy span; the metadata edit at t=30s doesn't
        // close or re-open it.
        entry.BusySeconds.ShouldBe(60);
        entry.IdleSeconds.ShouldBe(0);
        entry.WaitingForHumanSeconds.ShouldBe(0);
        entry.StateTransitions.ShouldBe(3); // includes the metadata edit
    }

    [Fact]
    public async Task GetWaitTimesAsync_OpenSpanAtWindowEnd_ClampedToWindow()
    {
        // Single transition at t=0s opens an Active span that never closes
        // within the window. Duration should clamp to (windowEnd - t=0) = 45s.
        var baseTime = DateTimeOffset.Parse("2026-04-10T12:00:00Z");
        var windowEnd = baseTime.AddSeconds(45);

        await SeedLifecycleAsync("agent:grace", new[]
        {
            (baseTime.AddSeconds(0), "Idle", "Active"),
        });

        var svc = new AnalyticsQueryService(_db);

        var result = await svc.GetWaitTimesAsync(
            sourceFilter: null,
            from: baseTime.AddSeconds(-1),
            to: windowEnd,
            TestContext.Current.CancellationToken);

        var entry = result.Entries.Single();
        entry.BusySeconds.ShouldBe(45);
        entry.StateTransitions.ShouldBe(1);
    }

    [Fact]
    public async Task GetWaitTimesAsync_SeparatesPerSource()
    {
        var baseTime = DateTimeOffset.Parse("2026-04-10T12:00:00Z");
        var windowEnd = baseTime.AddSeconds(120);

        await SeedLifecycleAsync("agent:ada", new[]
        {
            (baseTime.AddSeconds(0),  "Idle",   "Active"),
            (baseTime.AddSeconds(30), "Active", "Idle"),
        });
        await SeedLifecycleAsync("agent:grace", new[]
        {
            (baseTime.AddSeconds(0),  "Idle",   "Active"),
            (baseTime.AddSeconds(90), "Active", "Idle"),
        });

        var svc = new AnalyticsQueryService(_db);

        var result = await svc.GetWaitTimesAsync(
            sourceFilter: null,
            from: baseTime.AddSeconds(-1),
            to: windowEnd,
            TestContext.Current.CancellationToken);

        result.Entries.Count.ShouldBe(2);
        var ada = result.Entries.Single(e => e.Source == SourceHex("agent:ada"));
        ada.BusySeconds.ShouldBe(30);
        ada.IdleSeconds.ShouldBe(90); // 30 → windowEnd(120)

        var grace = result.Entries.Single(e => e.Source == SourceHex("agent:grace"));
        grace.BusySeconds.ShouldBe(90);
        grace.IdleSeconds.ShouldBe(30); // 90 → windowEnd(120)
    }

    [Fact]
    public async Task GetWaitTimesAsync_SourceFilter_NarrowsResults()
    {
        var baseTime = DateTimeOffset.Parse("2026-04-10T12:00:00Z");
        var windowEnd = baseTime.AddSeconds(60);

        await SeedLifecycleAsync("agent:ada", new[]
        {
            (baseTime.AddSeconds(0),  "Idle",   "Active"),
            (baseTime.AddSeconds(60), "Active", "Idle"),
        });
        await SeedLifecycleAsync("agent:grace", new[]
        {
            (baseTime.AddSeconds(0),  "Idle",   "Active"),
            (baseTime.AddSeconds(60), "Active", "Idle"),
        });

        var svc = new AnalyticsQueryService(_db);

        var result = await svc.GetWaitTimesAsync(
            sourceFilter: SourceHex("agent:ada"),
            from: baseTime.AddSeconds(-1),
            to: windowEnd,
            TestContext.Current.CancellationToken);

        result.Entries.Count.ShouldBe(1);
        result.Entries[0].Source.ShouldBe(SourceHex("agent:ada"));
    }

    private async Task SeedLifecycleAsync(
        string source,
        (DateTimeOffset timestamp, string from, string to)[] transitions)
    {
        foreach (var (ts, from, to) in transitions)
        {
            _db.ActivityEvents.Add(BuildLifecycle(source, ts, from, to));
        }
        await _db.SaveChangesAsync();
    }

    private static ActivityEventRecord BuildLifecycle(
        string source, DateTimeOffset timestamp, string from, string to)
    {
        // Per-test-case Guid identity derived deterministically from the
        // legacy slug-shaped source string so Equals-by-source semantics
        // continue to hold inside a single test method.
        return new ActivityEventRecord
        {
            Id = Guid.NewGuid(),
            SourceId = SourceGuid(source),
            EventType = nameof(ActivityEventType.StateChanged),
            Severity = "Info",
            Summary = $"State changed from {from} to {to}",
            Timestamp = timestamp,
            Details = JsonSerializer.SerializeToElement(new { from, to }),
        };
    }

    private static Guid SourceGuid(string source) => TestSlugIds.For(source);

    /// <summary>
    /// Returns the canonical hex form of <see cref="SourceGuid"/> — the
    /// shape <see cref="AnalyticsQueryService.GetWaitTimesAsync"/> emits as
    /// the source key on every <see cref="WaitTimeEntry"/> after #1629.
    /// </summary>
    private static string SourceHex(string source) =>
        Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(SourceGuid(source));
}