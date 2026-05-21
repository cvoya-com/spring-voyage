// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Orchestration;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Orchestration;
using Cvoya.Spring.Dapr.Routing;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Covers <see cref="MessagingToolHandlers"/> under the ADR-0048 / ADR-0049
/// delivery contract: <c>sv.messaging.send</c> / <c>sv.messaging.broadcast</c>
/// return a delivery acknowledgement, never the target's response; each call
/// emits a plain <see cref="ActivityEventType.MessageSent"/> activity (never a
/// DecisionMade); broadcast resolves a <c>scope</c> against the member graph.
/// </summary>
public class MessagingToolHandlersTests
{
    private static readonly Guid TenantId = OssTenantIds.Default;
    private static readonly Guid UnitId = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid ChildAgentId = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherChildAgentId = new("aaaaaaaa-0000-0000-0000-000000000002");

    private readonly IAgentProxyResolver _agentProxyResolver = Substitute.For<IAgentProxyResolver>();
    private readonly IActorProxyFactory _actorProxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly IOrchestrationTenantResolver _tenantResolver = Substitute.For<IOrchestrationTenantResolver>();
    private readonly IUnitMemberGraphStore _memberGraphStore = Substitute.For<IUnitMemberGraphStore>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly IThreadHopActor _hopActor = Substitute.For<IThreadHopActor>();
    private readonly RecordingThreadRegistry _threadRegistry = new();

    private readonly Dictionary<string, IAgent> _agents = new();
    private readonly List<ActivityEvent> _publishedEvents = [];
    private int _hopCount;

    public MessagingToolHandlersTests()
    {
        _agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>())
            .Returns(ci =>
            {
                var key = $"{ci.ArgAt<string>(0)}:{ci.ArgAt<string>(1)}";
                return _agents.TryGetValue(key, out var agent) ? agent : null;
            });

        _activityEventBus
            .PublishAsync(Arg.Do<ActivityEvent>(evt => _publishedEvents.Add(evt)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _tenantResolver.GetTenantForAddressAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(TenantId);

        _hopActor.IncrementAsync().Returns(_ => ++_hopCount);
        _actorProxyFactory
            .CreateActorProxy<IThreadHopActor>(Arg.Any<ActorId>(), nameof(ThreadHopActor))
            .Returns(_hopActor);
    }

    [Fact]
    public async Task HandleSend_DeliversAndReturnsAck()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        RegisterAgent(target);
        var message = CreateMessage();
        var threadId = Guid.NewGuid();

        var ack = await handlers.HandleSendAsync(
            caller, TenantId, target, message, reason: null, threadId, CancellationToken.None);

        // ADR-0049 — the ack is a delivery acknowledgement, not a reply.
        ack.Delivered.ShouldBeTrue();
        ack.Target.ShouldBe(target);
        ack.MessageId.ShouldBe(message.Id);
        ack.ThreadId.ShouldBe(threadId);
    }

    [Fact]
    public async Task HandleSend_DeliversMessageOnHopThread_NotUpstreamThread()
    {
        // #2596 — the delivered message lands on the (caller, target) hop
        // thread, never the caller's upstream conversation thread.
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        var agent = RegisterAgent(target);
        Message? delivered = null;
        agent.ReceiveAsync(Arg.Do<Message>(m => delivered = m), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        var message = CreateMessage();

        await handlers.HandleSendAsync(
            caller, TenantId, target, message, reason: null, Guid.NewGuid(), CancellationToken.None);

        delivered.ShouldNotBeNull();
        delivered!.ThreadId.ShouldNotBe(message.ThreadId);
        var hopThread = await _threadRegistry.GetOrCreateAsync(
            new[] { caller, target }, CancellationToken.None);
        delivered.ThreadId.ShouldBe(hopThread);
    }

    [Fact]
    public async Task HandleBroadcast_DeliversPerTargetHopThread()
    {
        // #2596 — a broadcast fans one message out to N targets; each
        // delivery is its own conversation hop and must land on the thread
        // of its own (caller, target) participant set.
        var handlers = CreateHandlers();
        var caller = Unit();
        var t1 = Agent(ChildAgentId);
        var t2 = Agent(OtherChildAgentId);
        var a1 = RegisterAgent(t1);
        var a2 = RegisterAgent(t2);
        Message? delivered1 = null;
        Message? delivered2 = null;
        a1.ReceiveAsync(Arg.Do<Message>(m => delivered1 = m), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        a2.ReceiveAsync(Arg.Do<Message>(m => delivered2 = m), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        await handlers.HandleBroadcastAsync(
            caller, TenantId, [t1, t2], scope: null, CreateMessage(), reason: null,
            Guid.NewGuid(), CancellationToken.None);

        delivered1.ShouldNotBeNull();
        delivered2.ShouldNotBeNull();
        delivered1!.ThreadId.ShouldNotBeNull();
        delivered2!.ThreadId.ShouldNotBeNull();
        delivered1.ThreadId.ShouldNotBe(delivered2.ThreadId);
        delivered1.ThreadId.ShouldBe(
            await _threadRegistry.GetOrCreateAsync(new[] { caller, t1 }, CancellationToken.None));
        delivered2.ThreadId.ShouldBe(
            await _threadRegistry.GetOrCreateAsync(new[] { caller, t2 }, CancellationToken.None));
    }

    [Fact]
    public async Task HandleSend_EmitsMessageSentActivity_NotDecisionMade()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        RegisterAgent(target);

        await handlers.HandleSendAsync(
            caller, TenantId, target, CreateMessage(), reason: null, Guid.NewGuid(), CancellationToken.None);

        _publishedEvents.Count.ShouldBe(1);
        _publishedEvents[0].EventType.ShouldBe(ActivityEventType.MessageSent);
        _publishedEvents[0].Source.ShouldBe(caller);
    }

    [Fact]
    public async Task HandleSend_IncrementsHopCounterOnce()
    {
        var handlers = CreateHandlers();
        var target = Agent(ChildAgentId);
        RegisterAgent(target);

        await handlers.HandleSendAsync(
            Unit(), TenantId, target, CreateMessage(), reason: null, Guid.NewGuid(), CancellationToken.None);

        await _hopActor.Received(1).IncrementAsync();
    }

    [Fact]
    public async Task HandleSend_SelfTarget_ThrowsSelfDelegation()
    {
        var handlers = CreateHandlers();

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleSendAsync(
                Unit(), TenantId, Unit(), CreateMessage(), reason: null, Guid.NewGuid(), CancellationToken.None));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationSelfDelegation);
    }

    [Fact]
    public async Task HandleSend_HopCycle_TerminatesAtMaxHopCount()
    {
        // A delivery cycle: every send on the thread increments the same
        // counter. The 17th hop on a limit-16 thread is rejected.
        var handlers = CreateHandlers(maxHopCount: 16);
        var target = Agent(ChildAgentId);
        RegisterAgent(target);
        var threadId = Guid.NewGuid();

        for (var i = 0; i < 16; i++)
        {
            await handlers.HandleSendAsync(
                Unit(), TenantId, target, CreateMessage(), reason: null, threadId, CancellationToken.None);
        }

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleSendAsync(
                Unit(), TenantId, target, CreateMessage(), reason: null, threadId, CancellationToken.None));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationDepthExceeded);
    }

    [Fact]
    public async Task HandleBroadcast_ExplicitAddresses_DeliversToEach()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var t1 = Agent(ChildAgentId);
        var t2 = Agent(OtherChildAgentId);
        RegisterAgent(t1);
        RegisterAgent(t2);

        var result = await handlers.HandleBroadcastAsync(
            caller, TenantId, [t1, t2], scope: null, CreateMessage(), reason: null, Guid.NewGuid(), CancellationToken.None);

        result.Deliveries.Count.ShouldBe(2);
        result.Deliveries.ShouldAllBe(d => d.Delivered);

        _publishedEvents.Count.ShouldBe(1);
        _publishedEvents[0].EventType.ShouldBe(ActivityEventType.MessageSent);
    }

    [Fact]
    public async Task HandleBroadcast_PartialFailure_ReportsPerTargetOutcome()
    {
        var handlers = CreateHandlers(maxHopCount: 16);
        var t1 = Agent(ChildAgentId);
        var t2 = Agent(OtherChildAgentId);
        var ok = RegisterAgent(t1);
        var failing = RegisterAgent(t2);
        failing.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("target unreachable"));

        var result = await handlers.HandleBroadcastAsync(
            Unit(), TenantId, [t1, t2], scope: null, CreateMessage(), reason: null, Guid.NewGuid(), CancellationToken.None);

        result.Deliveries.Count.ShouldBe(2);
        result.Deliveries.Single(d => d.Target == t1).Delivered.ShouldBeTrue();
        result.Deliveries.Single(d => d.Target == t2).Delivered.ShouldBeFalse();
        result.Deliveries.Single(d => d.Target == t2).Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task HandleBroadcast_AddressesAndScope_ThrowsValidation()
    {
        var handlers = CreateHandlers();

        var ex = await Should.ThrowAsync<OrchestrationException>(() =>
            handlers.HandleBroadcastAsync(
                Unit(), TenantId, [Agent(ChildAgentId)], BroadcastScope.UnitMembers,
                CreateMessage(), reason: null, Guid.NewGuid(), CancellationToken.None));

        ex.RejectCode.ShouldBe(OrchestrationException.RejectCodes.OrchestrationInvalidRequest);
    }

    [Fact]
    public async Task HandleBroadcast_UnitMembersScope_ResolvesCallerMembers()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var m1 = Agent(ChildAgentId);
        var m2 = Agent(OtherChildAgentId);
        RegisterAgent(m1);
        RegisterAgent(m2);
        _memberGraphStore.GetMembersAsync(UnitId, Arg.Any<CancellationToken>())
            .Returns(new[] { m1, m2 });

        var result = await handlers.HandleBroadcastAsync(
            caller, TenantId, explicitTargets: null, BroadcastScope.UnitMembers,
            CreateMessage(), reason: null, Guid.NewGuid(), CancellationToken.None);

        result.Deliveries.Count.ShouldBe(2);
        result.Deliveries.Select(d => d.Target).ShouldBe(new[] { m1, m2 }, ignoreOrder: true);
    }

    [Fact]
    public async Task HandleBroadcast_SiblingsScope_ResolvesParentMembersExcludingCaller()
    {
        // The caller's parent unit's members, minus the caller itself.
        var parentId = new Guid("cccccccc-0000-0000-0000-000000000001");
        var caller = Agent(ChildAgentId);
        var sibling = Agent(OtherChildAgentId);

        var membershipRepo = Substitute.For<IUnitMembershipRepository>();
        membershipRepo.ListByAgentAsync(ChildAgentId, Arg.Any<CancellationToken>())
            .Returns(new[] { new UnitMembership(parentId, ChildAgentId) });
        _memberGraphStore.GetMembersAsync(parentId, Arg.Any<CancellationToken>())
            .Returns(new[] { caller, sibling });
        RegisterAgent(sibling);

        var handlers = CreateHandlers(membershipRepo: membershipRepo);

        var result = await handlers.HandleBroadcastAsync(
            caller, TenantId, explicitTargets: null, BroadcastScope.Siblings,
            CreateMessage(), reason: null, Guid.NewGuid(), CancellationToken.None);

        // Only the sibling — the caller is excluded from its own sibling set.
        result.Deliveries.Count.ShouldBe(1);
        result.Deliveries[0].Target.ShouldBe(sibling);
    }

    private MessagingToolHandlers CreateHandlers(
        int maxHopCount = 16,
        IUnitMembershipRepository? membershipRepo = null)
    {
        var services = new ServiceCollection();
        if (membershipRepo is not null)
        {
            services.AddSingleton(membershipRepo);
        }

        services.AddSingleton(Substitute.For<IUnitSubunitMembershipRepository>());
        services.AddScoped<IThreadRegistry>(_ => _threadRegistry);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var deliveryService = new MessageDeliveryService(
            _agentProxyResolver,
            _actorProxyFactory,
            _tenantResolver,
            scopeFactory,
            Substitute.For<ILogger<MessageDeliveryService>>(),
            Options.Create(new OrchestrationDeliveryOptions
            {
                MaxAttempts = 3,
                Budget = TimeSpan.FromSeconds(2),
                InitialBackoff = TimeSpan.FromMilliseconds(1),
                MaxHopCount = maxHopCount,
            }));

        return new MessagingToolHandlers(
            deliveryService,
            _memberGraphStore,
            scopeFactory,
            _activityEventBus,
            Substitute.For<ILogger<MessagingToolHandlers>>());
    }

    private IAgent RegisterAgent(Address address)
    {
        var agent = Substitute.For<IAgent>();
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        _agents[$"{address.Scheme}:{address.Id:N}"] = agent;
        return agent;
    }

    private static Address Unit() => new(Address.UnitScheme, UnitId);

    private static Address Agent(Guid id) => new(Address.AgentScheme, id);

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
    /// Minimal in-memory <see cref="IThreadRegistry"/>: canonicalises the
    /// participant set and reuses the same Guid for repeated lookups so
    /// tests can assert participant-set thread identity (#2596 / ADR-0030).
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
