// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.Caching;

using Cvoya.Spring.Connector.GitHub.Caching;
using Cvoya.Spring.Connector.GitHub.Tests.RateLimit;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Exercises the in-memory <see cref="InMemoryGitHubResponseCache"/> — set /
/// get, TTL expiry, single-key invalidation, tag-based bulk invalidation, and
/// concurrent-access smoke.
/// </summary>
public class InMemoryGitHubResponseCacheTests
{
    private static InMemoryGitHubResponseCache CreateCache(
        FakeTimeProvider time,
        GitHubResponseCacheOptions? options = null)
    {
        return new InMemoryGitHubResponseCache(
            options ?? new GitHubResponseCacheOptions { CleanupInterval = TimeSpan.Zero },
            NullLoggerFactory.Instance,
            time);
    }

    private static CacheKey Key(string resource, string discriminator, params string[] tags) =>
        new(resource, discriminator, tags);

    [Fact]
    public async Task SetAsync_Get_ReturnsValue()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        using var cache = CreateCache(time);

        var key = Key("pr", "owner/repo#1", "pr:owner/repo#1");
        await cache.SetAsync(key, "hello", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var hit = await cache.TryGetAsync<string>(key, TestContext.Current.CancellationToken);

        hit.ShouldNotBeNull();
        hit!.Value.Value.ShouldBe("hello");
        hit.Value.Age.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task TryGetAsync_AfterTtl_ReturnsMiss()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        using var cache = CreateCache(time);

        var key = Key("pr", "owner/repo#1");
        await cache.SetAsync(key, "hello", TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(11));
        var hit = await cache.TryGetAsync<string>(key, TestContext.Current.CancellationToken);

        hit.ShouldBeNull();
    }

    [Fact]
    public async Task TryGetAsync_ReturnsAgeSinceWrite()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        using var cache = CreateCache(time);

        var key = Key("pr", "owner/repo#1");
        await cache.SetAsync(key, "hello", TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(5));
        var hit = await cache.TryGetAsync<string>(key, TestContext.Current.CancellationToken);

        hit.ShouldNotBeNull();
        hit!.Value.Age.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task InvalidateAsync_RemovesKey()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        using var cache = CreateCache(time);

        var key = Key("pr", "owner/repo#1", "pr:owner/repo#1");
        await cache.SetAsync(key, "hello", TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        await cache.InvalidateAsync(key, TestContext.Current.CancellationToken);

        (await cache.TryGetAsync<string>(key, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task InvalidateByTagAsync_RemovesAllEntriesSharingTheTag()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        using var cache = CreateCache(time);

        var a1 = Key("pr", "A/B#1", "pr:A/B#1");
        var a2 = Key("comments", "A/B#1", "pr:A/B#1", "issue:A/B#1");
        var a3 = Key("review_threads", "A/B#1", "pr:A/B#1");
        var b1 = Key("pr", "A/B#2", "pr:A/B#2");

        await cache.SetAsync(a1, 1, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        await cache.SetAsync(a2, 2, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        await cache.SetAsync(a3, 3, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        await cache.SetAsync(b1, 4, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        await cache.InvalidateByTagAsync("pr:A/B#1", TestContext.Current.CancellationToken);

        (await cache.TryGetAsync<int>(a1, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await cache.TryGetAsync<int>(a2, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await cache.TryGetAsync<int>(a3, TestContext.Current.CancellationToken)).ShouldBeNull();
        var remaining = await cache.TryGetAsync<int>(b1, TestContext.Current.CancellationToken);
        remaining.ShouldNotBeNull();
        remaining!.Value.Value.ShouldBe(4);
    }

    [Fact]
    public async Task InvalidateByTagAsync_UnknownTag_IsNoOp()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        using var cache = CreateCache(time);

        var k = Key("pr", "A/B#1", "pr:A/B#1");
        await cache.SetAsync(k, 42, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        await cache.InvalidateByTagAsync("pr:does/not-exist#9", TestContext.Current.CancellationToken);

        (await cache.TryGetAsync<int>(k, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task SetAsync_NonPositiveTtl_DoesNotCache()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        using var cache = CreateCache(time);

        var k = Key("pr", "A/B#1");
        await cache.SetAsync(k, "existing", TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);
        // An explicit zero TTL drops the entry entirely so subsequent reads miss.
        await cache.SetAsync(k, "new", TimeSpan.Zero, TestContext.Current.CancellationToken);

        (await cache.TryGetAsync<string>(k, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task TryGetAsync_TypeMismatch_TreatsAsMiss()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        using var cache = CreateCache(time);

        var k = Key("pr", "A/B#1");
        await cache.SetAsync(k, "hello", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        // Same key, different type — caller changed shape, treat as miss.
        var hit = await cache.TryGetAsync<int>(k, TestContext.Current.CancellationToken);
        hit.ShouldBeNull();
    }

    [Fact]
    public async Task SweepExpired_RemovesExpiredEntries()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        using var cache = CreateCache(time);

        var k1 = Key("pr", "A/B#1");
        var k2 = Key("pr", "A/B#2");
        await cache.SetAsync(k1, "short", TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        await cache.SetAsync(k2, "long", TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(5));
        cache.SweepExpired();

        (await cache.TryGetAsync<string>(k1, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await cache.TryGetAsync<string>(k2, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task ConcurrentReadsAndWrites_NoCorruption()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        using var cache = CreateCache(time);

        const int writers = 16;
        const int iterations = 200;

        // Smoke test — exercise the ConcurrentDictionary / tag-index lock
        // paths under parallel load to surface any obvious race. Not a
        // correctness proof; just verifies the cache stays consistent enough
        // that no await throws and every final write is observable.
        var tasks = Enumerable.Range(0, writers).Select(i => Task.Run(async () =>
        {
            for (var j = 0; j < iterations; j++)
            {
                var k = Key($"pr", $"A/B#{i}", $"pr:A/B#{i}", "repo:a/b");
                await cache.SetAsync(k, j, TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
                var hit = await cache.TryGetAsync<int>(k, TestContext.Current.CancellationToken);
                hit.ShouldNotBeNull();
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        for (var i = 0; i < writers; i++)
        {
            var hit = await cache.TryGetAsync<int>(Key("pr", $"A/B#{i}"), TestContext.Current.CancellationToken);
            hit.ShouldNotBeNull();
            hit!.Value.Value.ShouldBe(iterations - 1);
        }

        await cache.InvalidateByTagAsync("repo:a/b", TestContext.Current.CancellationToken);
        for (var i = 0; i < writers; i++)
        {
            (await cache.TryGetAsync<int>(Key("pr", $"A/B#{i}"), TestContext.Current.CancellationToken)).ShouldBeNull();
        }
    }
}