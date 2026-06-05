// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Messaging;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Messaging;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.Threads;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Covers <see cref="MessageDeliveryService"/> — the shared delivery seam:
/// the per-thread hop counter (#2576), the cross-tenant containment gate,
/// and the bounded-retry delivery loop (ADR-0049 §4).
/// </summary>
public class MessageDeliveryServiceTests
{
    private static readonly Guid TenantId = OssTenantIds.Default;
    private static readonly Guid OtherTenantId = new("ffffffff-0000-0000-0000-000000000099");

    private readonly IAgentProxyResolver _agentProxyResolver = Substitute.For<IAgentProxyResolver>();
    private readonly IMessageTenantResolver _tenantResolver = Substitute.For<IMessageTenantResolver>();
    private readonly RecordingThreadRegistry _threadRegistry = new();
    private readonly RecordingMessageWriter _messageWriter = new();
    private readonly List<RecordingDeliveryObserver> _deliveryObservers = new();
    private readonly ILogger<MessageDeliveryService> _logger =
        Substitute.For<ILogger<MessageDeliveryService>>();

    public MessageDeliveryServiceTests()
    {
        _tenantResolver.GetTenantForAddressAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(TenantId);
    }

    [Fact]
    public void EnsureNotSelfTarget_SameAddress_ThrowsSelfDelegation()
    {
        var address = new Address(Address.UnitScheme, Guid.NewGuid());

        var ex = Should.Throw<MessageDeliveryException>(() =>
            MessageDeliveryService.EnsureNotSelfTarget(address, address));

        ex.RejectCode.ShouldBe(MessageDeliveryException.RejectCodes.SelfDelivery);
    }

    [Fact]
    public async Task EnsureCallerTenant_Mismatch_ThrowsCrossTenant()
    {
        var caller = new Address(Address.UnitScheme, Guid.NewGuid());
        _tenantResolver.GetTenantForAddressAsync(caller, Arg.Any<CancellationToken>())
            .Returns(OtherTenantId);
        var service = CreateService();

        var ex = await Should.ThrowAsync<MessageDeliveryException>(() =>
            service.EnsureCallerTenantAsync(caller, TenantId, CancellationToken.None));

        ex.RejectCode.ShouldBe(MessageDeliveryException.RejectCodes.CrossTenant);
    }

    [Fact]
    public async Task DeliverWithRetry_TransientThenSuccess_DeliversWithinBudget()
    {
        var caller = new Address(Address.UnitScheme, Guid.NewGuid());
        var target = new Address(Address.AgentScheme, Guid.NewGuid());
        var agent = Substitute.For<IAgent>();
        var attempts = 0;

        _agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>()).Returns(agent);
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                attempts++;
                return attempts < 2
                    ? throw new InvalidOperationException("transient dapr hiccup")
                    : Task.FromResult<Message?>(null);
            });

        var service = CreateService();
        await service.DeliverWithRetryAsync(caller, target, CreateMessage(), CancellationToken.None);

        attempts.ShouldBe(2);
    }

    [Fact]
    public async Task DeliverWithRetry_ExhaustsBudget_ThrowsDeliveryFailed()
    {
        var caller = new Address(Address.UnitScheme, Guid.NewGuid());
        var target = new Address(Address.AgentScheme, Guid.NewGuid());
        var agent = Substitute.For<IAgent>();

        _agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>()).Returns(agent);
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns<Task<Message?>>(_ => throw new InvalidOperationException("dapr is down"));

        var service = CreateService();
        var ex = await Should.ThrowAsync<MessageDeliveryException>(() =>
            service.DeliverWithRetryAsync(caller, target, CreateMessage(), CancellationToken.None));

        ex.RejectCode.ShouldBe(MessageDeliveryException.RejectCodes.DeliveryFailed);
    }

    [Fact]
    public async Task DeliverWithRetry_FirstAttemptTimesOut_RetriesAndDelivers()
    {
        // #3004 — a hung proxy.ReceiveAsync (a busy actor blocking the enqueue
        // under load) must be bounded by PerAttemptTimeout and retried as a
        // transient failure, not left to hang ~100s on the Dapr actor proxy's
        // default HttpClient.Timeout. First attempt hangs past the per-attempt
        // deadline; the second enqueues normally.
        var caller = new Address(Address.UnitScheme, Guid.NewGuid());
        var target = new Address(Address.AgentScheme, Guid.NewGuid());
        var agent = Substitute.For<IAgent>();
        var attempts = 0;

        _agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>()).Returns(agent);
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                attempts++;
                if (attempts < 2)
                {
                    // Hang until the attempt-scoped token cancels.
                    await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                }

                return (Message?)null;
            });

        var service = CreateService(new MessageDeliveryOptions
        {
            MaxAttempts = 3,
            Budget = TimeSpan.FromSeconds(5),
            InitialBackoff = TimeSpan.FromMilliseconds(1),
            PerAttemptTimeout = TimeSpan.FromMilliseconds(50),
        });

        await service.DeliverWithRetryAsync(caller, target, CreateMessage(), CancellationToken.None);

        attempts.ShouldBe(2);
    }

    [Fact]
    public async Task DeliverWithRetry_EveryAttemptTimesOut_ThrowsDeliveryFailedFast()
    {
        // #3004 — a sustained hang surfaces a terminal delivery failure within
        // the budget (a few hundred ms here), never blocking ~100s on the Dapr
        // actor proxy's default HttpClient.Timeout.
        var caller = new Address(Address.UnitScheme, Guid.NewGuid());
        var target = new Address(Address.AgentScheme, Guid.NewGuid());
        var agent = Substitute.For<IAgent>();
        _agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>()).Returns(agent);
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return (Message?)null;
            });

        var service = CreateService(new MessageDeliveryOptions
        {
            MaxAttempts = 3,
            Budget = TimeSpan.FromMilliseconds(300),
            InitialBackoff = TimeSpan.FromMilliseconds(1),
            PerAttemptTimeout = TimeSpan.FromMilliseconds(50),
        });

        var started = DateTimeOffset.UtcNow;
        var ex = await Should.ThrowAsync<MessageDeliveryException>(() =>
            service.DeliverWithRetryAsync(caller, target, CreateMessage(), CancellationToken.None));
        var elapsed = DateTimeOffset.UtcNow - started;

        ex.RejectCode.ShouldBe(MessageDeliveryException.RejectCodes.DeliveryFailed);
        // Bounded by Budget — nowhere near the ~100s HttpClient.Timeout.
        elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DeliverWithRetry_CallerCancels_PropagatesOperationCanceled()
    {
        // #3004 — caller-initiated cancellation must propagate, never be
        // swallowed as a transient per-attempt timeout. The caller cancels
        // while an attempt is in flight; the linked attempt token trips, but
        // because the caller's token is cancelled the exception is rethrown.
        var caller = new Address(Address.UnitScheme, Guid.NewGuid());
        var target = new Address(Address.AgentScheme, Guid.NewGuid());
        var agent = Substitute.For<IAgent>();
        using var cts = new CancellationTokenSource();

        _agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>()).Returns(agent);
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, call.Arg<CancellationToken>());
                return (Message?)null;
            });

        var service = CreateService();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            service.DeliverWithRetryAsync(caller, target, CreateMessage(), cts.Token));
    }

    [Fact]
    public async Task DeliverWithRetry_ResolvesHopThreadFromCallerAndTarget()
    {
        // #2596 — the outbound message must carry the thread of the
        // (caller, target) hop, not the inbound message's upstream thread.
        var caller = new Address(Address.UnitScheme, Guid.NewGuid());
        var target = new Address(Address.AgentScheme, Guid.NewGuid());
        var agent = Substitute.For<IAgent>();
        Message? delivered = null;
        _agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>()).Returns(agent);
        agent.ReceiveAsync(Arg.Do<Message>(m => delivered = m), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        var inbound = CreateMessage();
        var service = CreateService();
        await service.DeliverWithRetryAsync(caller, target, inbound, CancellationToken.None);

        delivered.ShouldNotBeNull();
        // The hop thread is resolved from the (caller, target) participant
        // set — never the inbound message's thread.
        delivered!.ThreadId.ShouldNotBe(inbound.ThreadId);
        var hopThread = await _threadRegistry.GetOrCreateAsync(
            new[] { caller, target }, CancellationToken.None);
        delivered.ThreadId.ShouldBe(hopThread);
    }

    [Fact]
    public async Task DeliverWithRetry_DistinctTargets_GetDistinctHopThreads()
    {
        // #2596 — two deliveries from one caller to two different targets
        // are distinct conversation hops and must land on distinct threads.
        var caller = new Address(Address.UnitScheme, Guid.NewGuid());
        var targetA = new Address(Address.AgentScheme, Guid.NewGuid());
        var targetB = new Address(Address.AgentScheme, Guid.NewGuid());
        var agent = Substitute.For<IAgent>();
        var delivered = new List<Message>();
        _agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>()).Returns(agent);
        agent.ReceiveAsync(Arg.Do<Message>(m => delivered.Add(m)), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        var service = CreateService();
        await service.DeliverWithRetryAsync(caller, targetA, CreateMessage(), CancellationToken.None);
        await service.DeliverWithRetryAsync(caller, targetB, CreateMessage(), CancellationToken.None);

        delivered.Count.ShouldBe(2);
        delivered[0].ThreadId.ShouldNotBeNull();
        delivered[1].ThreadId.ShouldNotBeNull();
        delivered[0].ThreadId.ShouldNotBe(delivered[1].ThreadId);
    }

    [Fact]
    public async Task DeliverWithRetry_PersistsOutboundEnvelopeBeforeDelivery()
    {
        // #2764 — sv.messaging.send replies must land in spring.messages so
        // the recipient's inbox and the thread timeline see them. The
        // persistence call mirrors MessageRouter.RouteAsync: a scoped
        // IMessageWriter writes the outbound envelope (with the hop-resolved
        // thread id) before the actor proxy is invoked.
        var caller = new Address(Address.UnitScheme, Guid.NewGuid());
        var target = new Address(Address.AgentScheme, Guid.NewGuid());
        var agent = Substitute.For<IAgent>();
        Message? deliveredEnvelope = null;
        _agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>()).Returns(agent);
        agent.ReceiveAsync(Arg.Do<Message>(m => deliveredEnvelope = m), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        var service = CreateService();
        await service.DeliverWithRetryAsync(caller, target, CreateMessage(), CancellationToken.None);

        _messageWriter.Written.Count.ShouldBe(1);
        deliveredEnvelope.ShouldNotBeNull();
        // The persisted envelope and the delivered envelope are the same
        // record — same id, same hop-resolved thread, same From/To rewrite.
        _messageWriter.Written[0].Id.ShouldBe(deliveredEnvelope!.Id);
        _messageWriter.Written[0].ThreadId.ShouldBe(deliveredEnvelope.ThreadId);
        _messageWriter.Written[0].From.ShouldBe(caller);
        _messageWriter.Written[0].To.ShouldBe(target);
    }

    [Fact]
    public async Task DeliverWithRetry_TransientThenSuccess_PersistsOnce()
    {
        // The writer is idempotent on Message.Id, but the delivery loop
        // should persist before the retry loop — not inside it — so a
        // transient delivery failure does not produce duplicate write
        // attempts.
        var caller = new Address(Address.UnitScheme, Guid.NewGuid());
        var target = new Address(Address.AgentScheme, Guid.NewGuid());
        var agent = Substitute.For<IAgent>();
        var attempts = 0;
        _agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>()).Returns(agent);
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                attempts++;
                return attempts < 2
                    ? throw new InvalidOperationException("transient dapr hiccup")
                    : Task.FromResult<Message?>(null);
            });

        var service = CreateService();
        await service.DeliverWithRetryAsync(caller, target, CreateMessage(), CancellationToken.None);

        attempts.ShouldBe(2);
        _messageWriter.Written.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DeliverWithRetry_SuccessfulEnqueue_NotifiesEveryObserver()
    {
        // #2818 — the platform-side delivery wire-up: every registered
        // IConnectorDeliveryObserver is invoked once per successful mailbox
        // enqueue with the same caller / target / envelope / participant set
        // the delivery loop resolved. Multiple observers all fire (the Slack
        // connector registers one; future connectors register their own).
        var caller = new Address(Address.UnitScheme, Guid.NewGuid());
        var target = new Address(Address.AgentScheme, Guid.NewGuid());
        var agent = Substitute.For<IAgent>();
        _agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>()).Returns(agent);
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        var observerA = new RecordingDeliveryObserver();
        var observerB = new RecordingDeliveryObserver();
        _deliveryObservers.Add(observerA);
        _deliveryObservers.Add(observerB);

        var service = CreateService();
        var inbound = CreateMessage();
        await service.DeliverWithRetryAsync(caller, target, inbound, CancellationToken.None);

        observerA.Calls.Count.ShouldBe(1);
        observerB.Calls.Count.ShouldBe(1);

        observerA.Calls[0].Caller.ShouldBe(caller);
        observerA.Calls[0].Target.ShouldBe(target);
        observerA.Calls[0].Message.Id.ShouldBe(inbound.Id);
        // Default per-hop participant set is {caller, target}.
        observerA.Calls[0].Participants.Count.ShouldBe(2);
        observerA.Calls[0].Participants.ShouldContain(caller);
        observerA.Calls[0].Participants.ShouldContain(target);
    }

    [Fact]
    public async Task DeliverWithRetry_ObserverThrows_DeliveryStillSucceeds()
    {
        // Observers are best-effort — an observer that throws must not break
        // the delivery contract; the next observer must still fire. The
        // platform's delivery has already landed in the mailbox by the time
        // observers are invoked, so observer failures cannot un-deliver a
        // message.
        var caller = new Address(Address.UnitScheme, Guid.NewGuid());
        var target = new Address(Address.AgentScheme, Guid.NewGuid());
        var agent = Substitute.For<IAgent>();
        _agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>()).Returns(agent);
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        var throwing = new RecordingDeliveryObserver { ToThrow = new InvalidOperationException("slack down") };
        var quiet = new RecordingDeliveryObserver();
        _deliveryObservers.Add(throwing);
        _deliveryObservers.Add(quiet);

        var service = CreateService();
        await Should.NotThrowAsync(() => service.DeliverWithRetryAsync(
            caller, target, CreateMessage(), CancellationToken.None));

        // The throwing observer was still called; the quiet one fired after
        // the throw was swallowed.
        throwing.Calls.Count.ShouldBe(1);
        quiet.Calls.Count.ShouldBe(1);
    }

    [Fact]
    public async Task DeliverWithRetry_SharedThreadParticipants_PassedToObservers()
    {
        // sv.messaging.send shares a thread across all recipients. The
        // observer must see the full participant set the thread was keyed
        // against, not just the (caller, target) hop.
        var caller = new Address(Address.UnitScheme, Guid.NewGuid());
        var targetA = new Address(Address.AgentScheme, Guid.NewGuid());
        var targetB = new Address(Address.HumanScheme, Guid.NewGuid());
        var agent = Substitute.For<IAgent>();
        _agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>()).Returns(agent);
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        var observer = new RecordingDeliveryObserver();
        _deliveryObservers.Add(observer);

        var sharedSet = new[] { caller, targetA, targetB };
        var service = CreateService();
        await service.DeliverWithRetryAsync(
            caller, targetA, CreateMessage(), CancellationToken.None, sharedSet);

        observer.Calls.Count.ShouldBe(1);
        observer.Calls[0].Participants.Count.ShouldBe(3);
        observer.Calls[0].Participants.ShouldContain(targetB);
    }

    [Fact]
    public async Task DeliverWithRetry_RetryExhausted_DoesNotNotifyObservers()
    {
        // The observer represents successful delivery — a delivery that
        // fails every retry must not invoke any observer.
        var caller = new Address(Address.UnitScheme, Guid.NewGuid());
        var target = new Address(Address.AgentScheme, Guid.NewGuid());
        var agent = Substitute.For<IAgent>();
        _agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>()).Returns(agent);
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns<Task<Message?>>(_ => throw new InvalidOperationException("dapr is down"));

        var observer = new RecordingDeliveryObserver();
        _deliveryObservers.Add(observer);

        var service = CreateService();
        await Should.ThrowAsync<MessageDeliveryException>(() =>
            service.DeliverWithRetryAsync(caller, target, CreateMessage(), CancellationToken.None));

        observer.Calls.Count.ShouldBe(0);
    }

    [Fact]
    public async Task DeliverWithRetry_SameCallerAndTarget_ReuseHopThread()
    {
        // #2596 / ADR-0030 — the same participant set always resolves to the
        // same thread, so repeated deliveries between one pair stay on one
        // thread.
        var caller = new Address(Address.UnitScheme, Guid.NewGuid());
        var target = new Address(Address.AgentScheme, Guid.NewGuid());
        var agent = Substitute.For<IAgent>();
        var delivered = new List<Message>();
        _agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>()).Returns(agent);
        agent.ReceiveAsync(Arg.Do<Message>(m => delivered.Add(m)), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        var service = CreateService();
        await service.DeliverWithRetryAsync(caller, target, CreateMessage(), CancellationToken.None);
        await service.DeliverWithRetryAsync(caller, target, CreateMessage(), CancellationToken.None);

        delivered.Count.ShouldBe(2);
        delivered[0].ThreadId.ShouldBe(delivered[1].ThreadId);
    }

    private MessageDeliveryService CreateService(MessageDeliveryOptions? options = null) =>
        new(
            _agentProxyResolver,
            _tenantResolver,
            ScopeFactoryWith(_threadRegistry, _messageWriter),
            _logger,
            Options.Create(options ?? new MessageDeliveryOptions
            {
                MaxAttempts = 3,
                Budget = TimeSpan.FromSeconds(2),
                InitialBackoff = TimeSpan.FromMilliseconds(1),
            }));

    private IServiceScopeFactory ScopeFactoryWith(
        IThreadRegistry registry, IMessageWriter writer)
    {
        var services = new ServiceCollection();
        services.AddScoped<IThreadRegistry>(_ => registry);
        services.AddScoped<IMessageWriter>(_ => writer);
        foreach (var observer in _deliveryObservers)
        {
            // Snapshot the observer reference so the registration shares
            // the same instance the tests assert against.
            var captured = observer;
            services.AddSingleton<IConnectorDeliveryObserver>(_ => captured);
        }
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    private static Message CreateMessage() =>
        new(
            Guid.NewGuid(),
            new Address(Address.UnitScheme, Guid.NewGuid()),
            new Address(Address.AgentScheme, Guid.NewGuid()),
            MessageType.Domain,
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Content = "work" }),
            DateTimeOffset.UtcNow);

    /// <summary>
    /// Minimal in-memory <see cref="IMessageWriter"/>: records every call so
    /// tests can assert the outbound envelope was persisted before delivery
    /// (#2764). Re-write of an already-recorded id is silently no-op'd,
    /// matching the EF-backed writer's idempotency contract.
    /// </summary>
    private sealed class RecordingMessageWriter : IMessageWriter
    {
        public List<Message> Written { get; } = new();

        public Task WriteAsync(Message message, CancellationToken cancellationToken = default)
        {
            if (!Written.Any(m => m.Id == message.Id))
            {
                Written.Add(message);
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Records every <see cref="IConnectorDeliveryObserver.OnDeliveredAsync"/>
    /// invocation so #2818 tests can assert the delivery wire-up fires
    /// once per successful enqueue with the resolved participant set.
    /// </summary>
    private sealed class RecordingDeliveryObserver : IConnectorDeliveryObserver
    {
        public List<(Address Caller, Address Target, Message Message, IReadOnlyCollection<Address> Participants)> Calls { get; } = new();
        public Exception? ToThrow { get; set; }

        public Task OnDeliveredAsync(
            Address caller,
            Address target,
            Message message,
            IReadOnlyCollection<Address> threadParticipants,
            CancellationToken cancellationToken)
        {
            Calls.Add((caller, target, message, threadParticipants));
            if (ToThrow is not null)
            {
                throw ToThrow;
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Minimal in-memory <see cref="IThreadRegistry"/>: canonicalises the
    /// participant set and reuses the same Guid for repeated lookups so
    /// tests can assert participant-set identity (#2596 / ADR-0030).
    /// </summary>
    private sealed class RecordingThreadRegistry : IThreadRegistry
    {
        private readonly Dictionary<string, string> _byKey = new(StringComparer.Ordinal);

        public Task<string> GetOrCreateAsync(
            IEnumerable<Address> participants, CancellationToken cancellationToken = default)
        {
            var key = string.Join('|', participants
                .Select(a => $"{a.Scheme.ToLowerInvariant()}:{GuidFormatter.Format(a.Id)}")
                .OrderBy(s => s, StringComparer.Ordinal)
                .Distinct());
            if (!_byKey.TryGetValue(key, out var id))
            {
                id = GuidFormatter.Format(Guid.NewGuid());
                _byKey[key] = id;
            }

            return Task.FromResult(id);
        }

        public Task<ThreadRegistryEntry?> ResolveAsync(
            string threadId, CancellationToken cancellationToken = default)
            => Task.FromResult<ThreadRegistryEntry?>(null);

        public Task<string> EnsureThreadAsync(
            string threadId, IEnumerable<Address> participants, CancellationToken cancellationToken = default)
        {
            var key = string.Join('|', participants
                .Select(a => $"{a.Scheme.ToLowerInvariant()}:{GuidFormatter.Format(a.Id)}")
                .OrderBy(s => s, StringComparer.Ordinal)
                .Distinct());
            if (!_byKey.TryGetValue(key, out var id))
            {
                id = GuidFormatter.TryParse(threadId, out var supplied)
                    ? GuidFormatter.Format(supplied)
                    : GuidFormatter.Format(Guid.NewGuid());
                _byKey[key] = id;
            }

            return Task.FromResult(id);
        }
    }
}
