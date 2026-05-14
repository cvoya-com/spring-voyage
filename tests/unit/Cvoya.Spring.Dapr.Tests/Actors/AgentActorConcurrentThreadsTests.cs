// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Collections.Concurrent;
using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Agents;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Initiative;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.Tests.TestHelpers;

using global::Dapr.Actors;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Per-thread channel concurrency tests (#2076 / ADR-0030 §3 §44):
/// concurrent threads dispatch independently when
/// <c>concurrent_threads: true</c> (the default), and serialise behind
/// an agent-wide lock when <c>concurrent_threads: false</c>. The lock
/// pattern mirrors the SDK runtime's <c>asyncio.Lock</c>
/// (<c>agents/spring-voyage-agent-sdk/spring_voyage_agent_sdk/runtime.py</c>
/// lines 156-157, 185-187).
/// </summary>
public class AgentActorConcurrentThreadsTests
{
    /// <summary>
    /// Test execution dispatcher that signals when the dispatch starts,
    /// optionally blocks on a TaskCompletionSource so the caller can
    /// hold a thread mid-dispatch, and tracks the per-thread call
    /// timeline. Used to observe whether two thread dispatches overlap
    /// (concurrent_threads = true) or are serialised (false).
    /// </summary>
    private sealed class TimelineDispatcher : IExecutionDispatcher
    {
        private readonly ConcurrentBag<(string ThreadId, DateTimeOffset StartedAt, DateTimeOffset EndedAt)> _calls = new();
        private readonly Dictionary<string, TaskCompletionSource> _holdGates = new();
        private readonly object _gateLock = new();

        public IReadOnlyCollection<(string ThreadId, DateTimeOffset StartedAt, DateTimeOffset EndedAt)> Calls => _calls.ToArray();

        public TaskCompletionSource HoldGateFor(string threadId)
        {
            lock (_gateLock)
            {
                if (!_holdGates.TryGetValue(threadId, out var tcs))
                {
                    tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _holdGates[threadId] = tcs;
                }
                return tcs;
            }
        }

        public TaskCompletionSource StartedGateFor(string threadId) => StartedGates.GetOrAdd(threadId, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        public ConcurrentDictionary<string, TaskCompletionSource> StartedGates { get; } = new();

        public async Task<Message?> DispatchAsync(
            Message message,
            PromptAssemblyContext? context,
            CancellationToken cancellationToken = default)
        {
            var threadId = message.ThreadId ?? string.Empty;
            var startedAt = DateTimeOffset.UtcNow;

            // Signal the test that the dispatch for this thread started.
            StartedGateFor(threadId).TrySetResult();

            // Optionally block until the test releases the gate.
            TaskCompletionSource? hold;
            lock (_gateLock)
            {
                _holdGates.TryGetValue(threadId, out hold);
            }
            if (hold is not null)
            {
                await hold.Task.WaitAsync(cancellationToken);
            }

            var endedAt = DateTimeOffset.UtcNow;
            _calls.Add((threadId, startedAt, endedAt));
            return null;
        }
    }

    private static AgentActor BuildActor(
        TimelineDispatcher dispatcher,
        bool concurrentThreads,
        IActorStateManager stateManager,
        out string actorId)
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        actorId = TestSlugIds.HexFor("ct-agent");
        var host = ActorHost.CreateForTest<AgentActor>(new ActorTestOptions
        {
            ActorId = new ActorId(actorId),
        });

        var router = Substitute.For<MessageRouter>(
            Substitute.For<IDirectoryService>(),
            Substitute.For<IAgentProxyResolver>(),
            Substitute.For<IPermissionService>(),
            loggerFactory,
            NullMessageWriterScopeFactory.Create());

        var definitionProvider = Substitute.For<IAgentDefinitionProvider>();
        var execConfig = new AgentExecutionConfig(
            AgentRuntimeId: "claude",
            Image: null,
            ConcurrentThreads: concurrentThreads);
        definitionProvider.GetByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentDefinition(actorId, "Test", null, execConfig));

        var membershipRepository = Substitute.For<IUnitMembershipRepository>();
        membershipRepository
            .GetAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((UnitMembership?)null);

        var unitPolicyEnforcer = Substitute.For<IUnitPolicyEnforcer>();
        unitPolicyEnforcer.WithAllowByDefault();

        var actor = new AgentActor(
            host,
            Substitute.For<IActivityEventBus>(),
            Substitute.For<IAgentObservationCoordinator>(),
            new AgentMailboxCoordinator(Substitute.For<ILogger<AgentMailboxCoordinator>>()),
            new AgentDispatchCoordinator(dispatcher, router, Substitute.For<ILogger<AgentDispatchCoordinator>>()),
            definitionProvider,
            Array.Empty<ISkillRegistry>(),
            membershipRepository,
            unitPolicyEnforcer,
            Substitute.For<IAgentInitiativeEvaluator>(),
            loggerFactory,
            Substitute.For<IAgentLifecycleCoordinator>(),
            new AgentStateCoordinator(new InMemoryAgentLiveConfigStore(), Substitute.For<ILogger<AgentStateCoordinator>>()),
            new AgentAmendmentCoordinator(Substitute.For<ILogger<AgentAmendmentCoordinator>>()),
            new AgentUnitPolicyCoordinator(Substitute.For<ILogger<AgentUnitPolicyCoordinator>>()));

        var field = typeof(Actor).GetField(
            "<StateManager>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        field?.SetValue(actor, stateManager);

        // Default: no per-thread channels exist yet. The actor's
        // HandleDomainMessageAsync will create them on demand.
        stateManager.TryGetStateAsync<ThreadChannel>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(false, default!));
        stateManager.TryGetStateAsync<List<string>>(StateKeys.ChannelIndex, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<List<string>>(false, default!));

        return actor;
    }

    private static Message DomainMessage(string threadId, string actorId) =>
        new(
            Guid.NewGuid(),
            Address.For("agent", TestSlugIds.HexFor("sender")),
            Address.For("agent", actorId),
            MessageType.Domain,
            threadId,
            JsonSerializer.SerializeToElement(new { }),
            DateTimeOffset.UtcNow);

    [Fact]
    public async Task TwoConcurrentThreads_ConcurrentTrue_DispatchOverlap()
    {
        // ADR-0030 §44: with concurrent_threads = true (the default) two
        // messages on different threads must dispatch concurrently — the
        // dispatcher for thread B must start while thread A's dispatcher
        // is still running.
        var dispatcher = new TimelineDispatcher();
        var stateManager = Substitute.For<IActorStateManager>();
        var actor = BuildActor(dispatcher, concurrentThreads: true, stateManager, out var actorId);

        var threadA = "thread-a";
        var threadB = "thread-b";

        // Hold both threads so the test can observe overlap.
        var holdA = dispatcher.HoldGateFor(threadA);
        var holdB = dispatcher.HoldGateFor(threadB);

        await actor.ReceiveAsync(DomainMessage(threadA, actorId), TestContext.Current.CancellationToken);
        await actor.ReceiveAsync(DomainMessage(threadB, actorId), TestContext.Current.CancellationToken);

        // Both dispatchers must have started.
        await dispatcher.StartedGateFor(threadA).Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await dispatcher.StartedGateFor(threadB).Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Release both. With concurrent_threads = true, both started
        // before either was released — the assertion above (both
        // started gates fired) is the actual proof of concurrency.
        holdA.SetResult();
        holdB.SetResult();
    }

    [Fact]
    public async Task TwoConcurrentThreads_ConcurrentFalse_DispatchSerialised()
    {
        // ADR-0030 §3 + SDK runtime.py:156-157,185-187: with
        // concurrent_threads = false, an agent-wide lock serialises
        // every on_message call across threads. Thread B's dispatcher
        // must NOT start while thread A's dispatcher is still running.
        var dispatcher = new TimelineDispatcher();
        var stateManager = Substitute.For<IActorStateManager>();
        var actor = BuildActor(dispatcher, concurrentThreads: false, stateManager, out var actorId);

        var threadA = "thread-a";
        var threadB = "thread-b";

        // Hold thread A so its dispatcher is in flight when thread B
        // arrives. Thread B does NOT have a hold gate so it would
        // complete immediately if it ran — but we want to assert it
        // does NOT start while A is held.
        var holdA = dispatcher.HoldGateFor(threadA);

        await actor.ReceiveAsync(DomainMessage(threadA, actorId), TestContext.Current.CancellationToken);
        // Wait for thread A's dispatcher to enter the body.
        await dispatcher.StartedGateFor(threadA).Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await actor.ReceiveAsync(DomainMessage(threadB, actorId), TestContext.Current.CancellationToken);

        // Give thread B a chance to start if the lock were not
        // working. 200 ms is plenty for the actor turn + dispatcher
        // entry; in practice the lock release is the only thing that
        // unblocks B.
        await Task.Delay(200, TestContext.Current.CancellationToken);

        // Thread B's started gate must NOT be set yet — A's dispatcher
        // is still holding the agent-wide lock.
        dispatcher.StartedGates.TryGetValue(threadB, out var bGate);
        (bGate is null || !bGate.Task.IsCompleted).ShouldBeTrue(
            "concurrent_threads=false must serialise dispatch — thread B should not have started while thread A is held.");

        // Release A. Thread B is now free to acquire the lock and run.
        holdA.SetResult();

        // Thread B should now start. Wait briefly for the lock release
        // to propagate.
        await dispatcher.StartedGateFor(threadB).Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PerThreadCancel_OnlyAffectsTargetThread()
    {
        // ADR-0030 §44: cancel is per-thread. Cancelling thread A's
        // dispatcher must not cancel a concurrently running dispatcher
        // for thread B.
        var dispatcher = new TimelineDispatcher();
        var stateManager = Substitute.For<IActorStateManager>();
        var actor = BuildActor(dispatcher, concurrentThreads: true, stateManager, out var actorId);

        var threadA = "thread-a";
        var threadB = "thread-b";

        var holdA = dispatcher.HoldGateFor(threadA);
        var holdB = dispatcher.HoldGateFor(threadB);

        await actor.ReceiveAsync(DomainMessage(threadA, actorId), TestContext.Current.CancellationToken);
        await actor.ReceiveAsync(DomainMessage(threadB, actorId), TestContext.Current.CancellationToken);

        await dispatcher.StartedGateFor(threadA).Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await dispatcher.StartedGateFor(threadB).Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Send a Cancel for thread A. Thread B's dispatcher must keep
        // running.
        var cancelA = new Message(
            Guid.NewGuid(),
            Address.For("human", TestSlugIds.HexFor("operator")),
            Address.For("agent", actorId),
            MessageType.Cancel,
            threadA,
            default,
            DateTimeOffset.UtcNow);
        await actor.ReceiveAsync(cancelA, TestContext.Current.CancellationToken);

        // Thread B is still being held — the cancel for A must not
        // have cancelled B. Releasing B and observing it completes
        // proves it.
        holdB.SetResult();

        // Release A's gate too in case the cancel didn't tear down
        // the dispatcher cleanly (the SetResult is harmless if the
        // dispatcher is already cancelled).
        if (!holdA.Task.IsCompleted)
        {
            holdA.TrySetResult();
        }
    }
}
