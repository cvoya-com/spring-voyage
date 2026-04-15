// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.Caching;

using System.Text.Json;

using Cvoya.Spring.Connector.GitHub.Caching;
using Cvoya.Spring.Connector.GitHub.Tests.RateLimit;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

/// <summary>
/// Exercises <see cref="CachedSkillInvoker"/> read-through semantics —
/// first call invokes the factory; second call returns the cached result
/// without re-invoking. Also verifies opt-out behaviour via the no-op cache.
/// </summary>
public class CachedSkillInvokerTests
{
    private static CachedSkillInvoker CreateInvoker(
        IGitHubResponseCache cache,
        GitHubResponseCacheOptions? options = null)
    {
        return new CachedSkillInvoker(
            cache,
            options ?? new GitHubResponseCacheOptions { DefaultTtl = TimeSpan.FromMinutes(1) },
            NullLoggerFactory.Instance);
    }

    [Fact]
    public async Task InvokeAsync_FirstCallInvokesFactory_SecondCallHits()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new InMemoryGitHubResponseCache(
            new GitHubResponseCacheOptions { DefaultTtl = TimeSpan.FromMinutes(1), CleanupInterval = TimeSpan.Zero },
            NullLoggerFactory.Instance,
            time);

        var invoker = CreateInvoker(cache);

        var calls = 0;
        var json = JsonSerializer.SerializeToElement(new { answer = 42 });

        Task<JsonElement> Factory(CancellationToken _)
        {
            calls++;
            return Task.FromResult(json);
        }

        var r1 = await invoker.InvokeAsync("pr", "o/r#1", ["pr:o/r#1"], Factory, TestContext.Current.CancellationToken);
        var r2 = await invoker.InvokeAsync("pr", "o/r#1", ["pr:o/r#1"], Factory, TestContext.Current.CancellationToken);

        calls.ShouldBe(1);
        r1.GetProperty("answer").GetInt32().ShouldBe(42);
        r2.GetProperty("answer").GetInt32().ShouldBe(42);
    }

    [Fact]
    public async Task InvokeAsync_OptOut_AlwaysInvokesFactory()
    {
        var invoker = CreateInvoker(NoOpGitHubResponseCache.Instance);

        var calls = 0;
        Task<JsonElement> Factory(CancellationToken _)
        {
            calls++;
            return Task.FromResult(JsonSerializer.SerializeToElement(new { n = calls }));
        }

        await invoker.InvokeAsync("pr", "o/r#1", ["pr:o/r#1"], Factory, TestContext.Current.CancellationToken);
        await invoker.InvokeAsync("pr", "o/r#1", ["pr:o/r#1"], Factory, TestContext.Current.CancellationToken);

        calls.ShouldBe(2);
    }

    [Fact]
    public async Task InvokeAsync_DifferentDiscriminators_DoNotShareEntry()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new InMemoryGitHubResponseCache(
            new GitHubResponseCacheOptions { DefaultTtl = TimeSpan.FromMinutes(1), CleanupInterval = TimeSpan.Zero },
            NullLoggerFactory.Instance,
            time);

        var invoker = CreateInvoker(cache);

        var calls = 0;
        Task<JsonElement> Factory(CancellationToken _)
        {
            calls++;
            return Task.FromResult(JsonSerializer.SerializeToElement(new { n = calls }));
        }

        await invoker.InvokeAsync("pr", "o/r#1", ["pr:o/r#1"], Factory, TestContext.Current.CancellationToken);
        await invoker.InvokeAsync("pr", "o/r#2", ["pr:o/r#2"], Factory, TestContext.Current.CancellationToken);

        calls.ShouldBe(2);
    }

    [Fact]
    public async Task InvokeAsync_UsesPerResourceTtl()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        using var cache = new InMemoryGitHubResponseCache(
            new GitHubResponseCacheOptions
            {
                DefaultTtl = TimeSpan.FromHours(1),
                // Short TTL for "comments"; read should miss after 6s.
                Ttls = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
                {
                    ["comments"] = TimeSpan.FromSeconds(5),
                },
                CleanupInterval = TimeSpan.Zero,
            },
            NullLoggerFactory.Instance,
            time);

        var invoker = new CachedSkillInvoker(
            cache,
            new GitHubResponseCacheOptions
            {
                DefaultTtl = TimeSpan.FromHours(1),
                Ttls = new Dictionary<string, TimeSpan>(StringComparer.Ordinal)
                {
                    ["comments"] = TimeSpan.FromSeconds(5),
                },
            },
            NullLoggerFactory.Instance);

        var calls = 0;
        Task<JsonElement> Factory(CancellationToken _)
        {
            calls++;
            return Task.FromResult(JsonSerializer.SerializeToElement(new { n = calls }));
        }

        await invoker.InvokeAsync("comments", "o/r#1", ["issue:o/r#1"], Factory, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(6));
        await invoker.InvokeAsync("comments", "o/r#1", ["issue:o/r#1"], Factory, TestContext.Current.CancellationToken);

        calls.ShouldBe(2);
    }
}