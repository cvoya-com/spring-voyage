// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.RateLimit;

using Cvoya.Spring.Connector.GitHub.RateLimit;

using Shouldly;

using Xunit;

public class InMemoryRateLimitStateStoreTests
{
    [Fact]
    public async Task Write_ThenRead_ReturnsSameSnapshot()
    {
        var store = new InMemoryRateLimitStateStore();
        var reset = DateTimeOffset.UtcNow.AddMinutes(30);
        var snapshot = new RateLimitSnapshot(
            Remaining: 4900,
            Limit: 5000,
            ResetAt: reset,
            UpdatedAt: DateTimeOffset.UtcNow);

        await store.WriteAsync("core", "inst-1", snapshot, TestContext.Current.CancellationToken);

        var read = await store.ReadAsync("core", "inst-1", TestContext.Current.CancellationToken);

        read.ShouldNotBeNull();
        read!.Remaining.ShouldBe(4900);
        read.Limit.ShouldBe(5000);
        read.ResetAt.ShouldBe(reset);
    }

    [Fact]
    public async Task Read_MissingResource_ReturnsNull()
    {
        var store = new InMemoryRateLimitStateStore();
        var read = await store.ReadAsync("graphql", "inst-1", TestContext.Current.CancellationToken);
        read.ShouldBeNull();
    }

    [Fact]
    public async Task ReadAll_ReturnsOnlyResourcesForInstallation()
    {
        var store = new InMemoryRateLimitStateStore();
        var now = DateTimeOffset.UtcNow;

        await store.WriteAsync("core", "inst-1",
            new RateLimitSnapshot(4000, 5000, now.AddMinutes(30), now),
            TestContext.Current.CancellationToken);
        await store.WriteAsync("graphql", "inst-1",
            new RateLimitSnapshot(4900, 5000, now.AddMinutes(60), now),
            TestContext.Current.CancellationToken);
        await store.WriteAsync("core", "inst-2",
            new RateLimitSnapshot(1, 60, now.AddMinutes(5), now),
            TestContext.Current.CancellationToken);

        var all = await store.ReadAllAsync("inst-1", TestContext.Current.CancellationToken);

        all.Count.ShouldBe(2);
        all.Keys.ShouldBe(new[] { "core", "graphql" }, ignoreOrder: true);
        all["core"].Remaining.ShouldBe(4000);
        all["graphql"].Remaining.ShouldBe(4900);
    }

    [Fact]
    public async Task ReadAll_UnknownInstallation_ReturnsEmpty()
    {
        var store = new InMemoryRateLimitStateStore();
        var all = await store.ReadAllAsync("inst-nope", TestContext.Current.CancellationToken);
        all.ShouldBeEmpty();
    }

    [Fact]
    public async Task WriteAsync_ConcurrentWrites_ConvergeToLastWrite()
    {
        var store = new InMemoryRateLimitStateStore();
        var now = DateTimeOffset.UtcNow;

        // Simulate a hundred concurrent writers decrementing remaining.
        var tasks = Enumerable.Range(0, 100)
            .Select(i => store.WriteAsync(
                "core",
                "inst-1",
                new RateLimitSnapshot(5000 - i, 5000, now.AddMinutes(30), now),
                TestContext.Current.CancellationToken))
            .ToList();
        await Task.WhenAll(tasks);

        var read = await store.ReadAsync("core", "inst-1", TestContext.Current.CancellationToken);
        read.ShouldNotBeNull();
        // Last-writer-wins: the surviving snapshot is one of the ones
        // written above. Value must be in the valid range.
        read!.Remaining.ShouldBeInRange(4900, 5000);
    }
}