// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Tests;

using Cvoya.Spring.Connector.Slack.Auth.OAuth;

using Shouldly;

using Xunit;

/// <summary>
/// Pins the in-memory store's contract: save then consume returns the
/// entry; consume twice returns null on the second call (state is
/// single-use); expired entries are dropped at consume time.
/// </summary>
public class InMemorySlackOAuthStateStoreTests
{
    [Fact]
    public async Task SaveThenConsume_ReturnsEntry()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new InMemorySlackOAuthStateStore();
        var entry = new SlackOAuthStateEntry(
            State: "state-1",
            Scopes: "commands",
            RedirectUri: "https://example.test/cb",
            ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(5),
            ClientState: null);

        await sut.SaveAsync(entry, ct);

        var fetched = await sut.ConsumeAsync("state-1", ct);
        fetched.ShouldNotBeNull();
        fetched!.State.ShouldBe("state-1");
    }

    [Fact]
    public async Task ConsumeAsync_TwiceForSameState_SecondReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new InMemorySlackOAuthStateStore();
        var entry = new SlackOAuthStateEntry(
            "state-2",
            "commands",
            "https://example.test/cb",
            DateTimeOffset.UtcNow.AddMinutes(5),
            null);
        await sut.SaveAsync(entry, ct);

        (await sut.ConsumeAsync("state-2", ct)).ShouldNotBeNull();
        (await sut.ConsumeAsync("state-2", ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ConsumeAsync_ExpiredEntry_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var sut = new InMemorySlackOAuthStateStore(time);

        var entry = new SlackOAuthStateEntry(
            "state-3",
            "commands",
            "https://example.test/cb",
            time.GetUtcNow().AddMinutes(1),
            null);
        await sut.SaveAsync(entry, ct);

        // Advance past expiry.
        time.Advance(TimeSpan.FromMinutes(2));

        (await sut.ConsumeAsync("state-3", ct)).ShouldBeNull();
    }

    [Fact]
    public async Task ConsumeAsync_UnknownState_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var sut = new InMemorySlackOAuthStateStore();

        (await sut.ConsumeAsync("never-saved", ct)).ShouldBeNull();
    }

    /// <summary>
    /// Minimal manual <see cref="TimeProvider"/> for the expiry test
    /// — avoids pulling in <c>Microsoft.Extensions.Time.Testing</c>
    /// just for one fake-clock case.
    /// </summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public ManualTimeProvider(DateTimeOffset start)
        {
            _now = start;
        }

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now = _now.Add(delta);
    }
}
