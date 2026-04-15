// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.RateLimit;

using System.Net.Http;

using Cvoya.Spring.Connector.GitHub.RateLimit;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tracker tests that cover the <see cref="IRateLimitStateStore"/>
/// integration layer added for #240. The existing in-memory-only
/// behaviors are covered by <see cref="GitHubRateLimitTrackerTests"/>.
/// </summary>
public class GitHubRateLimitTrackerPersistenceTests
{
    private static HttpResponseMessage ResponseWithQuota(
        string resource,
        int limit,
        int remaining,
        DateTimeOffset reset)
    {
        var response = new HttpResponseMessage();
        response.Headers.Add("x-ratelimit-limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.Add("x-ratelimit-remaining", remaining.ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.Add("x-ratelimit-reset", reset.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        response.Headers.Add("x-ratelimit-resource", resource);
        return response;
    }

    private static GitHubRateLimitTracker CreateTracker(
        IRateLimitStateStore store,
        RateLimitStateStoreOptions? options = null,
        FakeTimeProvider? time = null) =>
        new(
            new GitHubRetryOptions(),
            store,
            Options.Create(options ?? new RateLimitStateStoreOptions()),
            NullLoggerFactory.Instance,
            time);

    [Fact]
    public async Task UpdateFromHeaders_WritesThroughToStateStore()
    {
        var store = new InMemoryRateLimitStateStore();
        var tracker = CreateTracker(store);
        var reset = DateTimeOffset.UtcNow.AddMinutes(30);

        using var response = ResponseWithQuota("graphql", 5000, 4987, reset);
        tracker.UpdateFromHeaders(response.Headers);

        // In-memory is updated.
        tracker.GetQuota("graphql")!.Remaining.ShouldBe(4987);

        // State store is also updated synchronously.
        var persisted = await store.ReadAsync("graphql", "_default", TestContext.Current.CancellationToken);
        persisted.ShouldNotBeNull();
        persisted!.Remaining.ShouldBe(4987);
        persisted.Limit.ShouldBe(5000);
        persisted.ResetAt.ToUnixTimeSeconds().ShouldBe(reset.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task SeedFromStateStoreAsync_SeedsInMemoryView()
    {
        var store = new InMemoryRateLimitStateStore();
        var now = DateTimeOffset.UtcNow;
        await store.WriteAsync(
            "core",
            "_default",
            new RateLimitSnapshot(Remaining: 4000, Limit: 5000, ResetAt: now.AddMinutes(30), UpdatedAt: now),
            TestContext.Current.CancellationToken);
        await store.WriteAsync(
            "graphql",
            "_default",
            new RateLimitSnapshot(Remaining: 50, Limit: 5000, ResetAt: now.AddMinutes(60), UpdatedAt: now),
            TestContext.Current.CancellationToken);

        var tracker = CreateTracker(store);

        tracker.GetQuota("core").ShouldBeNull();
        tracker.GetQuota("graphql").ShouldBeNull();

        await tracker.SeedFromStateStoreAsync(TestContext.Current.CancellationToken);

        tracker.GetQuota("core").ShouldNotBeNull();
        tracker.GetQuota("core")!.Remaining.ShouldBe(4000);
        tracker.GetQuota("graphql").ShouldNotBeNull();
        tracker.GetQuota("graphql")!.Remaining.ShouldBe(50);
    }

    [Fact]
    public async Task SeedFromStateStoreAsync_DoesNotRollBackFresherLocalObservations()
    {
        var store = new InMemoryRateLimitStateStore();
        var start = DateTimeOffset.Parse("2025-01-01T00:00:00Z");
        var time = new FakeTimeProvider(start);
        var tracker = CreateTracker(store, time: time);

        // Stale persisted snapshot (observed 1h ago at 100 remaining).
        await store.WriteAsync(
            "core",
            "_default",
            new RateLimitSnapshot(Remaining: 100, Limit: 5000, ResetAt: start.AddMinutes(30), UpdatedAt: start.AddHours(-1)),
            TestContext.Current.CancellationToken);

        // Fresh local observation (now, at 4000 remaining).
        using var response = ResponseWithQuota("core", 5000, 4000, start.AddMinutes(30));
        tracker.UpdateFromHeaders(response.Headers);

        await tracker.SeedFromStateStoreAsync(TestContext.Current.CancellationToken);

        // The newer in-memory snapshot must win.
        tracker.GetQuota("core")!.Remaining.ShouldBe(4000);
    }

    [Fact]
    public void UpdateFromHeaders_StateStoreWriteFails_TrackerStaysInMemory()
    {
        var store = Substitute.For<IRateLimitStateStore>();
        store
            .WriteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<RateLimitSnapshot>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("store unavailable")));

        var tracker = CreateTracker(store);
        var reset = DateTimeOffset.UtcNow.AddMinutes(30);

        using var response = ResponseWithQuota("core", 5000, 4200, reset);

        // Must NOT throw — tracker absorbs the persistence failure.
        Should.NotThrow(() => tracker.UpdateFromHeaders(response.Headers));

        // In-memory view is still updated.
        tracker.GetQuota("core")!.Remaining.ShouldBe(4200);
    }

    [Fact]
    public async Task SeedFromStateStoreAsync_StateStoreReadFails_LeavesTrackerEmpty()
    {
        var store = Substitute.For<IRateLimitStateStore>();
        store
            .ReadAllAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyDictionary<string, RateLimitSnapshot>>(
                new InvalidOperationException("store unavailable")));

        var tracker = CreateTracker(store);

        // Must NOT throw.
        await Should.NotThrowAsync(() => tracker.SeedFromStateStoreAsync(TestContext.Current.CancellationToken));

        tracker.GetQuota("core").ShouldBeNull();
    }

    [Fact]
    public async Task UpdateFromHeaders_ConcurrentCallers_LastWriterWinsInStore()
    {
        var store = new InMemoryRateLimitStateStore();
        var tracker = CreateTracker(store);
        var reset = DateTimeOffset.UtcNow.AddMinutes(30);

        Parallel.For(0, 100, i =>
        {
            using var response = ResponseWithQuota("core", 5000, 5000 - i, reset);
            tracker.UpdateFromHeaders(response.Headers);
        });

        var persisted = await store.ReadAsync("core", "_default", TestContext.Current.CancellationToken);
        persisted.ShouldNotBeNull();
        // The surviving row is whichever write won the race.
        persisted!.Remaining.ShouldBeInRange(4900, 5000);

        // In-memory and persisted should point at the same snapshot shape.
        var quota = tracker.GetQuota("core");
        quota!.Limit.ShouldBe(persisted.Limit);
        quota.Reset.ToUnixTimeSeconds().ShouldBe(persisted.ResetAt.ToUnixTimeSeconds());
    }

    [Fact]
    public void Legacy_TwoArgConstructor_DefaultsToInMemoryStore()
    {
        // The legacy constructor (options + loggerFactory) must still
        // produce a tracker whose GetQuota works without any explicit
        // store setup. This guards the source-compat path used by the
        // existing GitHubRateLimitTrackerTests.
        var tracker = new GitHubRateLimitTracker(new GitHubRetryOptions(), NullLoggerFactory.Instance);
        using var response = ResponseWithQuota("core", 5000, 4000, DateTimeOffset.UtcNow.AddMinutes(30));
        tracker.UpdateFromHeaders(response.Headers);
        tracker.GetQuota("core")!.Remaining.ShouldBe(4000);
    }
}