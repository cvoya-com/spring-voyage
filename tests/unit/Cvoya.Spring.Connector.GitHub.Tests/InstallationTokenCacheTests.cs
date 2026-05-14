// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests;

using Cvoya.Spring.Connector.GitHub.Auth;
using Cvoya.Spring.Connector.GitHub.Tests.RateLimit;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Exercises the in-memory <see cref="InstallationTokenCache"/> — freshness,
/// proactive refresh, single-flight coalescing, and mint failures.
/// </summary>
public class InstallationTokenCacheTests
{
    private static InstallationTokenCache CreateCache(
        FakeTimeProvider time,
        InstallationTokenCacheOptions? options = null)
    {
        return new InstallationTokenCache(
            options ?? new InstallationTokenCacheOptions(),
            NullLoggerFactory.Instance,
            time);
    }

    [Fact]
    public async Task GetOrMintAsync_FirstCall_InvokesMintAndReturnsToken()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        var cache = CreateCache(time);

        var mintCalls = 0;
        Task<InstallationAccessToken> Mint(long id, CancellationToken _)
        {
            mintCalls++;
            return Task.FromResult(new InstallationAccessToken($"tok-{id}", now.AddHours(1)));
        }

        var result = await cache.GetOrMintAsync(42, Mint, TestContext.Current.CancellationToken);

        result.Token.ShouldBe("tok-42");
        mintCalls.ShouldBe(1);
    }

    [Fact]
    public async Task GetOrMintAsync_Cached_DoesNotRemint()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        var cache = CreateCache(time);

        var mintCalls = 0;
        Task<InstallationAccessToken> Mint(long id, CancellationToken _)
        {
            mintCalls++;
            return Task.FromResult(new InstallationAccessToken($"tok-{id}-{mintCalls}", now.AddHours(1)));
        }

        var first = await cache.GetOrMintAsync(42, Mint, TestContext.Current.CancellationToken);
        var second = await cache.GetOrMintAsync(42, Mint, TestContext.Current.CancellationToken);

        mintCalls.ShouldBe(1);
        first.Token.ShouldBe(second.Token);
    }

    [Fact]
    public async Task GetOrMintAsync_ProactiveRefreshWindow_RefreshesBeforeExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        var cache = CreateCache(time, new InstallationTokenCacheOptions
        {
            ProactiveRefreshWindow = TimeSpan.FromSeconds(60),
        });

        var mintCalls = 0;
        Task<InstallationAccessToken> Mint(long id, CancellationToken _)
        {
            mintCalls++;
            return Task.FromResult(new InstallationAccessToken(
                $"tok-{mintCalls}",
                time.GetUtcNow().AddHours(1)));
        }

        // First mint — token expires in 1 hour.
        await cache.GetOrMintAsync(42, Mint, TestContext.Current.CancellationToken);

        // Advance so ~90s remain — still outside the 60s window.
        time.Advance(TimeSpan.FromMinutes(58) + TimeSpan.FromSeconds(30));
        await cache.GetOrMintAsync(42, Mint, TestContext.Current.CancellationToken);
        mintCalls.ShouldBe(1);

        // Advance another 45s — now ~45s remain, well within 60s window;
        // the next call must refresh rather than return the stale token.
        time.Advance(TimeSpan.FromSeconds(45));
        var refreshed = await cache.GetOrMintAsync(42, Mint, TestContext.Current.CancellationToken);

        mintCalls.ShouldBe(2);
        refreshed.Token.ShouldBe("tok-2");
    }

    [Fact]
    public async Task GetOrMintAsync_Expired_RemintsNewToken()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        var cache = CreateCache(time);

        var mintCalls = 0;
        Task<InstallationAccessToken> Mint(long id, CancellationToken _)
        {
            mintCalls++;
            return Task.FromResult(new InstallationAccessToken(
                $"tok-{mintCalls}",
                time.GetUtcNow().AddHours(1)));
        }

        await cache.GetOrMintAsync(42, Mint, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(70));
        await cache.GetOrMintAsync(42, Mint, TestContext.Current.CancellationToken);

        mintCalls.ShouldBe(2);
    }

    [Fact]
    public async Task GetOrMintAsync_ConcurrentCallers_SingleMint()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        var cache = CreateCache(time);

        var mintStarted = new TaskCompletionSource();
        var releaseMint = new TaskCompletionSource();
        var mintCalls = 0;

        async Task<InstallationAccessToken> Mint(long id, CancellationToken _)
        {
            Interlocked.Increment(ref mintCalls);
            mintStarted.TrySetResult();
            // Hold the mint until both callers are parked on the semaphore.
            await releaseMint.Task;
            return new InstallationAccessToken("tok", now.AddHours(1));
        }

        var call1 = cache.GetOrMintAsync(42, Mint, TestContext.Current.CancellationToken);
        await mintStarted.Task; // first call is inside the critical section
        var call2 = cache.GetOrMintAsync(42, Mint, TestContext.Current.CancellationToken);

        // Second caller must be queued on the semaphore, not minting yet.
        await Task.Delay(50, TestContext.Current.CancellationToken);
        mintCalls.ShouldBe(1);

        releaseMint.SetResult();

        var r1 = await call1;
        var r2 = await call2;

        mintCalls.ShouldBe(1);
        r1.Token.ShouldBe(r2.Token);
    }

    [Fact]
    public async Task GetOrMintAsync_MintFails_PropagatesError_NoCache()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        var cache = CreateCache(time);

        var mintCalls = 0;
        Task<InstallationAccessToken> Mint(long id, CancellationToken _)
        {
            mintCalls++;
            throw new InvalidOperationException("boom");
        }

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            cache.GetOrMintAsync(42, Mint, TestContext.Current.CancellationToken));
        ex.Message.ShouldBe("boom");

        // Retry — should NOT be cached, so mint runs again.
        await Should.ThrowAsync<InvalidOperationException>(() =>
            cache.GetOrMintAsync(42, Mint, TestContext.Current.CancellationToken));

        mintCalls.ShouldBe(2);
    }

    [Fact]
    public async Task Invalidate_ForcesRemint()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        var cache = CreateCache(time);

        var mintCalls = 0;
        Task<InstallationAccessToken> Mint(long id, CancellationToken _)
        {
            mintCalls++;
            return Task.FromResult(new InstallationAccessToken($"tok-{mintCalls}", now.AddHours(1)));
        }

        await cache.GetOrMintAsync(42, Mint, TestContext.Current.CancellationToken);
        cache.Invalidate(42);
        await cache.GetOrMintAsync(42, Mint, TestContext.Current.CancellationToken);

        mintCalls.ShouldBe(2);
    }

    [Fact]
    public async Task GetOrMintAsync_CeilingTtl_CapsEffectiveExpiry()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        var cache = CreateCache(time, new InstallationTokenCacheOptions
        {
            CeilingTtl = TimeSpan.FromMinutes(30),
            ProactiveRefreshWindow = TimeSpan.FromSeconds(60),
        });

        var mintCalls = 0;
        Task<InstallationAccessToken> Mint(long id, CancellationToken _)
        {
            mintCalls++;
            // GitHub hands out a 2-hour token (hypothetical).
            return Task.FromResult(new InstallationAccessToken(
                $"tok-{mintCalls}",
                time.GetUtcNow().AddHours(2)));
        }

        await cache.GetOrMintAsync(42, Mint, TestContext.Current.CancellationToken);

        // Advance past the 30m ceiling — cache must re-mint.
        time.Advance(TimeSpan.FromMinutes(31));
        await cache.GetOrMintAsync(42, Mint, TestContext.Current.CancellationToken);

        mintCalls.ShouldBe(2);
    }
}
