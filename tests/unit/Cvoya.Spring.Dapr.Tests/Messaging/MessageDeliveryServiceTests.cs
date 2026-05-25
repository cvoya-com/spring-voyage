// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Messaging;

using System.Text.Json;

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
    private readonly IActorProxyFactory _actorProxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly IMessageTenantResolver _tenantResolver = Substitute.For<IMessageTenantResolver>();
    private readonly RecordingThreadRegistry _threadRegistry = new();
    private readonly RecordingMessageWriter _messageWriter = new();
    private readonly ILogger<MessageDeliveryService> _logger =
        Substitute.For<ILogger<MessageDeliveryService>>();

    public MessageDeliveryServiceTests()
    {
        _tenantResolver.GetTenantForAddressAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(TenantId);
    }

    [Fact]
    public async Task EnsureHopBudget_UnderLimit_DoesNotThrow()
    {
        var hopActor = StubHopActor(1, 2, 3);
        var service = CreateService(maxHopCount: 16);

        // Three hops on the same thread, all under the limit.
        await service.EnsureHopBudgetAsync(Guid.NewGuid(), CancellationToken.None);
        await service.EnsureHopBudgetAsync(Guid.NewGuid(), CancellationToken.None);
        await service.EnsureHopBudgetAsync(Guid.NewGuid(), CancellationToken.None);

        await hopActor.Received(3).IncrementAsync();
    }

    [Fact]
    public async Task EnsureHopBudget_ExceedsLimit_ThrowsDepthExceeded()
    {
        // The hop actor reports a count past the limit — the cycle terminates.
        StubHopActor(17);
        var service = CreateService(maxHopCount: 16);

        var ex = await Should.ThrowAsync<MessageDeliveryException>(() =>
            service.EnsureHopBudgetAsync(Guid.NewGuid(), CancellationToken.None));

        ex.RejectCode.ShouldBe(MessageDeliveryException.RejectCodes.DepthExceeded);
    }

    [Fact]
    public async Task EnsureHopBudget_AtLimit_DoesNotThrow()
    {
        // Hop count exactly at the limit is still permitted; only a count
        // strictly past it is rejected.
        StubHopActor(16);
        var service = CreateService(maxHopCount: 16);

        await Should.NotThrowAsync(() =>
            service.EnsureHopBudgetAsync(Guid.NewGuid(), CancellationToken.None));
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

    private IThreadHopActor StubHopActor(params int[] returnsSequence)
    {
        var hopActor = Substitute.For<IThreadHopActor>();
        var queue = new Queue<int>(returnsSequence);
        hopActor.IncrementAsync().Returns(_ => queue.Count > 0 ? queue.Dequeue() : returnsSequence[^1]);
        _actorProxyFactory
            .CreateActorProxy<IThreadHopActor>(Arg.Any<ActorId>(), nameof(ThreadHopActor))
            .Returns(hopActor);
        return hopActor;
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

    private MessageDeliveryService CreateService(int maxHopCount = 16) =>
        new(
            _agentProxyResolver,
            _actorProxyFactory,
            _tenantResolver,
            ScopeFactoryWith(_threadRegistry, _messageWriter),
            _logger,
            Options.Create(new MessageDeliveryOptions
            {
                MaxAttempts = 3,
                Budget = TimeSpan.FromSeconds(2),
                InitialBackoff = TimeSpan.FromMilliseconds(1),
                MaxHopCount = maxHopCount,
            }));

    private static IServiceScopeFactory ScopeFactoryWith(
        IThreadRegistry registry, IMessageWriter writer)
    {
        var services = new ServiceCollection();
        services.AddScoped<IThreadRegistry>(_ => registry);
        services.AddScoped<IMessageWriter>(_ => writer);
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
    }
}
