// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.GitHub.Tests.OAuth;

using Cvoya.Spring.Connector.GitHub.Auth.OAuth;
using Cvoya.Spring.Connector.GitHub.Tests.RateLimit;

using Microsoft.Extensions.Logging.Abstractions;

using Shouldly;

using Xunit;

public class InMemoryOAuthStateStoreTests
{
    [Fact]
    public async Task ConsumeAsync_HappyPath_ReturnsAndDeletesEntry()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        var store = new InMemoryOAuthStateStore(NullLoggerFactory.Instance, time);
        var ct = TestContext.Current.CancellationToken;

        var entry = new OAuthStateEntry("state-abc", "repo", "https://example.com/cb", now.AddMinutes(5), ClientState: null);
        await store.SaveAsync(entry, ct);

        var consumed = await store.ConsumeAsync("state-abc", ct);

        consumed.ShouldNotBeNull();
        consumed!.State.ShouldBe("state-abc");

        // Second consume returns null — one-time-use semantics.
        var replay = await store.ConsumeAsync("state-abc", ct);
        replay.ShouldBeNull();
    }

    [Fact]
    public async Task ConsumeAsync_ExpiredEntry_ReturnsNull()
    {
        var now = DateTimeOffset.UtcNow;
        var time = new FakeTimeProvider(now);
        var store = new InMemoryOAuthStateStore(NullLoggerFactory.Instance, time);
        var ct = TestContext.Current.CancellationToken;

        var entry = new OAuthStateEntry("state-exp", "repo", "https://example.com/cb", now.AddMinutes(1), ClientState: null);
        await store.SaveAsync(entry, ct);

        time.Advance(TimeSpan.FromMinutes(2));

        var consumed = await store.ConsumeAsync("state-exp", ct);
        consumed.ShouldBeNull();
    }

    [Fact]
    public async Task ConsumeAsync_UnknownState_ReturnsNull()
    {
        var store = new InMemoryOAuthStateStore(NullLoggerFactory.Instance, new FakeTimeProvider(DateTimeOffset.UtcNow));
        var ct = TestContext.Current.CancellationToken;

        var result = await store.ConsumeAsync("does-not-exist", ct);

        result.ShouldBeNull();
    }
}