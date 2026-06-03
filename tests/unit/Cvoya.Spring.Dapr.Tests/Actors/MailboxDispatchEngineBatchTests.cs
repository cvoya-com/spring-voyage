// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Text.Json;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Initiative;

using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for the batched-delivery behaviour of
/// <see cref="MailboxDispatchEngine"/> (#3056): on activation the engine
/// delivers a thread's pending messages as one ordered batch, records the
/// in-flight count, removes the whole batch atomically on drain, re-arms the
/// next bounded batch with messages that arrived during the turn, and caps the
/// batch at <see cref="MailboxDispatchEngine.MaxBatchSize"/>.
/// <para>
/// The engine + <see cref="IMailboxHost"/> are internal; this test compiles
/// against them via <c>InternalsVisibleTo</c>. It drives the real
/// <see cref="AgentMailboxCoordinator"/> routing and an in-memory state store,
/// and captures every batch the host is asked to run.
/// </para>
/// </summary>
public class MailboxDispatchEngineBatchTests
{
    private const string AgentId = "aaaaaaaa111111111111111111111111";
    private const string ThreadId = "thread-batch-001";

    private static readonly Address Sender =
        new(Address.HumanScheme, new Guid("11111111-1111-1111-1111-111111111111"));
    private static readonly Address Agent =
        new(Address.AgentScheme, new Guid("aaaaaaaa-1111-1111-1111-111111111111"));

    [Fact]
    public async Task Drain_AfterTurn_DeliversMessagesThatArrivedDuringItAsOneBatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var (engine, host) = CreateEngine();

        // First inbound activates the thread: a one-message batch dispatches.
        var m1 = CreateMessage();
        await engine.HandleInboundAsync(m1, ct);
        await DrainPendingDispatchAsync(engine);

        host.DispatchedBatches.Count.ShouldBe(1);
        host.DispatchedBatches[0].Select(m => m.Id).ShouldBe([m1.Id]);

        // Two more arrive while the turn is in flight — they append behind the
        // in-flight head (Case 2); they do NOT each launch a dispatcher.
        var m2 = CreateMessage();
        var m3 = CreateMessage();
        await engine.HandleInboundAsync(m2, ct);
        await engine.HandleInboundAsync(m3, ct);
        host.DispatchedBatches.Count.ShouldBe(1, "queued messages must wait for the drain, not spawn parallel turns.");

        // The turn returns: the drain removes the whole in-flight batch (m1)
        // and re-arms with the accumulated set {m2, m3} as ONE batch.
        await engine.DrainAsync(ThreadId, "turn done", ct);
        await DrainPendingDispatchAsync(engine);

        host.DispatchedBatches.Count.ShouldBe(2);
        host.DispatchedBatches[1].Select(m => m.Id).ShouldBe([m2.Id, m3.Id]);

        // Draining the second batch empties and removes the channel.
        await engine.DrainAsync(ThreadId, "turn done", ct);
        (await host.ReadChannelAsync(ThreadId, ct)).ShouldBeNull("an empty channel is removed after drain.");
    }

    [Fact]
    public async Task Drain_RemovesExactlyTheInFlightBatch_PreservingLaterArrivals()
    {
        var ct = TestContext.Current.CancellationToken;
        var (engine, host) = CreateEngine();

        var m1 = CreateMessage();
        await engine.HandleInboundAsync(m1, ct);
        await DrainPendingDispatchAsync(engine);

        // In-flight count is recorded as the dispatched batch size (1 here).
        var channel = await host.ReadChannelAsync(ThreadId, ct);
        channel.ShouldNotBeNull();
        channel!.InFlightCount.ShouldBe(1);
        channel.Messages.Count.ShouldBe(1);

        // A late arrival appends behind the in-flight head.
        var m2 = CreateMessage();
        await engine.HandleInboundAsync(m2, ct);

        // Drain removes only the one in-flight message; m2 survives as the
        // next batch and InFlightCount is recomputed for it.
        await engine.DrainAsync(ThreadId, "turn done", ct);
        await DrainPendingDispatchAsync(engine);

        channel = await host.ReadChannelAsync(ThreadId, ct);
        channel.ShouldNotBeNull();
        channel!.Messages.Select(m => m.Id).ShouldBe([m2.Id]);
        channel.InFlightCount.ShouldBe(1);
    }

    [Fact]
    public async Task Activation_CapsTheBatchAtMaxBatchSize_RemainderFormsNextBatch()
    {
        var ct = TestContext.Current.CancellationToken;
        var (engine, host) = CreateEngine();

        // Seed an idle channel that has accumulated more than one batch worth
        // of messages (e.g. a burst delivered before the actor drained).
        var overflow = MailboxDispatchEngine.MaxBatchSize + 7;
        var seeded = new List<Message>();
        for (var i = 0; i < overflow - 1; i++)
        {
            seeded.Add(CreateMessage());
        }
        await host.SeedIdleChannelAsync(ThreadId, seeded, ct);

        // One more inbound restarts the idle channel (Case 3) and triggers a
        // batched dispatch.
        var trigger = CreateMessage();
        seeded.Add(trigger);
        await engine.HandleInboundAsync(trigger, ct);
        await DrainPendingDispatchAsync(engine);

        // The first batch is capped; the remainder stays queued.
        host.DispatchedBatches.Count.ShouldBe(1);
        host.DispatchedBatches[0].Count.ShouldBe(MailboxDispatchEngine.MaxBatchSize);
        host.DispatchedBatches[0].Select(m => m.Id)
            .ShouldBe(seeded.Take(MailboxDispatchEngine.MaxBatchSize).Select(m => m.Id));

        // Draining the first batch re-arms with the remaining 7.
        await engine.DrainAsync(ThreadId, "turn done", ct);
        await DrainPendingDispatchAsync(engine);

        host.DispatchedBatches.Count.ShouldBe(2);
        host.DispatchedBatches[1].Count.ShouldBe(overflow - MailboxDispatchEngine.MaxBatchSize);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static (MailboxDispatchEngine Engine, FakeMailboxHost Host) CreateEngine()
    {
        var host = new FakeMailboxHost(CreateInMemoryStateManager());
        var coordinator = new AgentMailboxCoordinator(NullLogger<AgentMailboxCoordinator>.Instance);
        var engine = new MailboxDispatchEngine(
            host, coordinator, definitionProvider: null, NullLogger.Instance);
        host.Engine = engine;
        return (engine, host);
    }

    /// <summary>
    /// Awaits the engine's most-recently-launched dispatch task so the captured
    /// batch is observable. The fire-and-forget dispatcher assigns
    /// <see cref="MailboxDispatchEngine.PendingDispatchTask"/> synchronously,
    /// so awaiting it after the launching call settles the capture.
    /// </summary>
    private static async Task DrainPendingDispatchAsync(MailboxDispatchEngine engine)
    {
        var pending = engine.PendingDispatchTask;
        if (pending is not null)
        {
            await pending;
        }
    }

    private static Message CreateMessage() => new(
        Guid.NewGuid(),
        Sender,
        Agent,
        MessageType.Domain,
        ThreadId,
        JsonSerializer.SerializeToElement(new { content = "hi" }),
        DateTimeOffset.UtcNow);

    /// <summary>
    /// An <see cref="IActorStateManager"/> substitute backed by an in-memory
    /// dictionary for the three operations the engine uses (TryGet / Set /
    /// TryRemove of the per-thread channel and the channel index). Reference
    /// semantics are sufficient here — the engine always re-saves a channel
    /// after mutating it, and <c>TakeBatch</c> copies, so the in-flight batch
    /// is independent of later appends.
    /// </summary>
    private static IActorStateManager CreateInMemoryStateManager()
    {
        var store = new Dictionary<string, object?>(StringComparer.Ordinal);
        var sm = Substitute.For<IActorStateManager>();

        sm.TryGetStateAsync<ThreadChannel>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => store.TryGetValue(ci.ArgAt<string>(0), out var v) && v is ThreadChannel tc
                ? new ConditionalValue<ThreadChannel>(true, tc)
                : new ConditionalValue<ThreadChannel>(false, null!));
        sm.TryGetStateAsync<List<string>>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => store.TryGetValue(ci.ArgAt<string>(0), out var v) && v is List<string> idx
                ? new ConditionalValue<List<string>>(true, idx)
                : new ConditionalValue<List<string>>(false, null!));

        sm.SetStateAsync(Arg.Any<string>(), Arg.Any<ThreadChannel>(), Arg.Any<CancellationToken>())
            .Returns(ci => { store[ci.ArgAt<string>(0)] = ci.ArgAt<ThreadChannel>(1); return Task.CompletedTask; });
        sm.SetStateAsync(Arg.Any<string>(), Arg.Any<List<string>>(), Arg.Any<CancellationToken>())
            .Returns(ci => { store[ci.ArgAt<string>(0)] = ci.ArgAt<List<string>>(1); return Task.CompletedTask; });

        sm.TryRemoveStateAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => store.Remove(ci.ArgAt<string>(0)));

        return sm;
    }

    private sealed class FakeMailboxHost(IActorStateManager stateManager) : IMailboxHost
    {
        public List<IReadOnlyList<Message>> DispatchedBatches { get; } = [];
        public LifecycleStatus Lifecycle { get; set; } = LifecycleStatus.Running;
        public MailboxDispatchEngine? Engine { get; set; }

        public string ActorId => AgentId;
        public IActorStateManager StateManager => stateManager;

        public Task<LifecycleStatus> GetLifecycleStatusAsync(CancellationToken ct) =>
            Task.FromResult(Lifecycle);

        public Task<AgentMetadata> ResolveEffectiveMetadataAsync(Message message, CancellationToken ct) =>
            Task.FromResult(new AgentMetadata(Enabled: true));

        public Task<(AgentMetadata Effective, PolicyVerdict? Verdict)> ApplyUnitPoliciesAsync(
            AgentMetadata effective, CancellationToken ct) =>
            Task.FromResult((effective, (PolicyVerdict?)null));

        public Task InvokeRuntimeAsync(
            IReadOnlyList<Message> batch,
            AgentMetadata effective,
            Func<string, Task> onDispatchExit,
            CancellationToken ct)
        {
            // Capture the batch the engine asked to run. The test drives the
            // drain explicitly via DrainAsync, so we do not invoke
            // onDispatchExit here.
            DispatchedBatches.Add(batch);
            return Task.CompletedTask;
        }

        public Task SignalDispatchExitAsync(string threadId, string reason) =>
            Engine!.DrainAsync(threadId, reason, CancellationToken.None);

        public Task EmitActivityAsync(ActivityEvent activityEvent, CancellationToken ct) =>
            Task.CompletedTask;

        public async Task<ThreadChannel?> ReadChannelAsync(string threadId, CancellationToken ct)
        {
            var result = await stateManager
                .TryGetStateAsync<ThreadChannel>(StateKeys.ChannelPrefix + threadId, ct);
            return result.HasValue ? result.Value : null;
        }

        public async Task SeedIdleChannelAsync(string threadId, List<Message> messages, CancellationToken ct)
        {
            var channel = new ThreadChannel
            {
                ThreadId = threadId,
                Messages = [.. messages],
                Dispatching = false,
                InFlightCount = 0,
            };
            await stateManager.SetStateAsync(StateKeys.ChannelPrefix + threadId, channel, ct);
            var index = new List<string> { threadId };
            await stateManager.SetStateAsync(StateKeys.ChannelIndex, index, ct);
        }
    }
}
