// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Messaging;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
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
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Covers <see cref="MessagingToolHandlers"/> under the ADR-0048 / ADR-0049
/// delivery contract reshaped by #2747:
/// <list type="bullet">
///   <item><c>sv.messaging.send</c> takes <c>recipients[]</c> (or scope) and
///   delivers to all on the SHARED thread <c>{caller} ∪ recipients</c>.</item>
///   <item><c>sv.messaging.multicast</c> takes the same input and delivers to
///   each on its OWN 1-1 thread.</item>
///   <item>Both reject <c>connector://</c> recipients with
///   <see cref="MessageDeliveryException.RejectCodes.UnroutableTarget"/>.</item>
///   <item>Each call emits a single
///   <see cref="ActivityEventType.MessageSent"/> activity.</item>
/// </list>
/// </summary>
public class MessagingToolHandlersTests
{
    private static readonly Guid TenantId = OssTenantIds.Default;
    private static readonly Guid UnitId = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid ChildAgentId = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherChildAgentId = new("aaaaaaaa-0000-0000-0000-000000000002");

    private readonly IAgentProxyResolver _agentProxyResolver = Substitute.For<IAgentProxyResolver>();
    private readonly IMessageTenantResolver _tenantResolver = Substitute.For<IMessageTenantResolver>();
    private readonly IUnitMemberGraphStore _memberGraphStore = Substitute.For<IUnitMemberGraphStore>();
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly RecordingThreadRegistry _threadRegistry = new();
    private readonly IMessageQueryService _messageQuery = Substitute.For<IMessageQueryService>();

    private readonly Dictionary<string, IAgent> _agents = new();
    private readonly List<ActivityEvent> _publishedEvents = [];

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
    }

    [Fact]
    public async Task HandleSend_SingleRecipient_DeliversAndReturnsAck()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        RegisterAgent(target);

        var result = await handlers.HandleSendAsync(
            caller, TenantId, [target], scope: null, CreateMessage(),
            reason: null, threadId: Guid.NewGuid(), CancellationToken.None);

        // ADR-0049 — the ack is a delivery acknowledgement, never the recipient's reply.
        result.Deliveries.Count.ShouldBe(1);
        result.Deliveries[0].Delivered.ShouldBeTrue();
        result.Deliveries[0].Target.ShouldBe(target);
        result.MessageId.ShouldNotBe(Guid.Empty);

        // The shared thread is {caller, target} for a single-recipient send.
        var expectedThread = await _threadRegistry.GetOrCreateAsync(
            new[] { caller, target }, CancellationToken.None);
        result.Deliveries[0].ThreadId.ShouldBe(ParseGuid(expectedThread));
        result.ThreadId.ShouldBe(ParseGuid(expectedThread));
    }

    [Fact]
    public async Task HandleSend_MultipleRecipients_AllLandOnSameSharedThread()
    {
        // #2747 — sv.messaging.send with multiple recipients delivers every
        // copy onto the SINGLE shared thread {caller ∪ recipients}. Any one
        // recipient sees the others on the same thread; sv.memory.history_with
        // by any participant returns this thread's timeline.
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

        var result = await handlers.HandleSendAsync(
            caller, TenantId, [t1, t2], scope: null, CreateMessage(),
            reason: null, threadId: Guid.NewGuid(), CancellationToken.None);

        delivered1.ShouldNotBeNull();
        delivered2.ShouldNotBeNull();
        delivered1!.ThreadId.ShouldBe(delivered2!.ThreadId);

        var sharedThread = await _threadRegistry.GetOrCreateAsync(
            new[] { caller, t1, t2 }, CancellationToken.None);
        delivered1.ThreadId.ShouldBe(sharedThread);
        delivered2.ThreadId.ShouldBe(sharedThread);
        result.ThreadId.ShouldBe(ParseGuid(sharedThread));
    }

    [Fact]
    public async Task HandleMulticast_DeliversPerRecipientHopThread()
    {
        // #2747 — sv.messaging.multicast keeps the per-pair-thread semantic:
        // each recipient lands on its own 1-1 thread {caller, recipient}
        // and never sees the others in its envelope.
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

        var result = await handlers.HandleMulticastAsync(
            caller, TenantId, [t1, t2], scope: null, CreateMessage(),
            reason: null, threadId: Guid.NewGuid(), CancellationToken.None);

        delivered1.ShouldNotBeNull();
        delivered2.ShouldNotBeNull();
        delivered1!.ThreadId.ShouldNotBe(delivered2!.ThreadId);
        delivered1.ThreadId.ShouldBe(
            await _threadRegistry.GetOrCreateAsync(new[] { caller, t1 }, CancellationToken.None));
        delivered2.ThreadId.ShouldBe(
            await _threadRegistry.GetOrCreateAsync(new[] { caller, t2 }, CancellationToken.None));

        // The MulticastResult per-recipient row reports the pair thread the
        // recipient landed on.
        result.Deliveries.Count.ShouldBe(2);
        result.Deliveries.Single(d => d.Target == t1).ThreadId
            .ShouldBe(ParseGuid(delivered1.ThreadId!));
        result.Deliveries.Single(d => d.Target == t2).ThreadId
            .ShouldBe(ParseGuid(delivered2.ThreadId!));
    }

    [Fact]
    public async Task HandleSend_EmitsMessageSentActivity_NotDecisionMade()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var target = Agent(ChildAgentId);
        RegisterAgent(target);

        await handlers.HandleSendAsync(
            caller, TenantId, [target], scope: null, CreateMessage(),
            reason: null, threadId: Guid.NewGuid(), CancellationToken.None);

        _publishedEvents.Count.ShouldBe(1);
        _publishedEvents[0].EventType.ShouldBe(ActivityEventType.MessageSent);
        _publishedEvents[0].Source.ShouldBe(caller);
    }

    [Fact]
    public async Task HandleSend_CallerOnlyRecipient_ThrowsInvalidRequestAfterDedupe()
    {
        // #2747 — recipients is a set; the caller is auto-included, so
        // explicitly listing them is a no-op. A list whose only entry is
        // the caller dedupes to empty and surfaces as InvalidRequest.
        var handlers = CreateHandlers();
        var caller = Unit();

        var ex = await Should.ThrowAsync<MessageDeliveryException>(() =>
            handlers.HandleSendAsync(
                caller, TenantId, [caller], scope: null, CreateMessage(),
                reason: null, threadId: Guid.NewGuid(), CancellationToken.None));

        ex.RejectCode.ShouldBe(MessageDeliveryException.RejectCodes.InvalidRequest);
    }

    [Fact]
    public async Task HandleSend_CallerListedAmongRecipients_SilentlyDeduped()
    {
        // #2747 — listing yourself alongside other recipients is fine: the
        // caller is dropped from the recipient set silently and delivery
        // proceeds for the others. The thread is still {caller} ∪ recipients.
        var handlers = CreateHandlers();
        var caller = Unit();
        var t1 = Agent(ChildAgentId);
        var t2 = Agent(OtherChildAgentId);
        RegisterAgent(t1);
        RegisterAgent(t2);

        var result = await handlers.HandleSendAsync(
            caller, TenantId, [caller, t1, t2], scope: null, CreateMessage(),
            reason: null, threadId: Guid.NewGuid(), CancellationToken.None);

        result.Deliveries.Count.ShouldBe(2);
        result.Deliveries.Select(d => d.Target).ShouldBe(new[] { t1, t2 }, ignoreOrder: true);
        result.Deliveries.ShouldAllBe(d => d.Delivered);

        // Shared thread is {caller, t1, t2} — listing the caller didn't change it.
        var sharedThread = await _threadRegistry.GetOrCreateAsync(
            new[] { caller, t1, t2 }, CancellationToken.None);
        result.ThreadId.ShouldBe(ParseGuid(sharedThread));
    }

    [Fact]
    public async Task HandleSend_ConnectorRecipient_ThrowsUnroutableTarget()
    {
        // #2747 — connector:// addresses are non-routable senders; sv.messaging.*
        // rejects them with an explicit reject code so the calling model gets
        // a usable error instead of a silent failure.
        var handlers = CreateHandlers();
        var connector = new Address(Address.ConnectorScheme, Guid.NewGuid());

        var ex = await Should.ThrowAsync<MessageDeliveryException>(() =>
            handlers.HandleSendAsync(
                Unit(), TenantId, [connector], scope: null, CreateMessage(),
                reason: null, threadId: Guid.NewGuid(), CancellationToken.None));

        ex.RejectCode.ShouldBe(MessageDeliveryException.RejectCodes.UnroutableTarget);
    }

    [Fact]
    public async Task HandleMulticast_ConnectorRecipient_ThrowsUnroutableTarget()
    {
        var handlers = CreateHandlers();
        var connector = new Address(Address.ConnectorScheme, Guid.NewGuid());

        var ex = await Should.ThrowAsync<MessageDeliveryException>(() =>
            handlers.HandleMulticastAsync(
                Unit(), TenantId, [connector], scope: null, CreateMessage(),
                reason: null, threadId: Guid.NewGuid(), CancellationToken.None));

        ex.RejectCode.ShouldBe(MessageDeliveryException.RejectCodes.UnroutableTarget);
    }

    [Fact]
    public async Task HandleMulticast_ExplicitRecipients_DeliversToEach()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var t1 = Agent(ChildAgentId);
        var t2 = Agent(OtherChildAgentId);
        RegisterAgent(t1);
        RegisterAgent(t2);

        var result = await handlers.HandleMulticastAsync(
            caller, TenantId, [t1, t2], scope: null, CreateMessage(),
            reason: null, threadId: Guid.NewGuid(), CancellationToken.None);

        result.Deliveries.Count.ShouldBe(2);
        result.Deliveries.ShouldAllBe(d => d.Delivered);

        _publishedEvents.Count.ShouldBe(1);
        _publishedEvents[0].EventType.ShouldBe(ActivityEventType.MessageSent);
    }

    [Fact]
    public async Task HandleMulticast_PartialFailure_ReportsPerRecipientOutcome()
    {
        var handlers = CreateHandlers();
        var t1 = Agent(ChildAgentId);
        var t2 = Agent(OtherChildAgentId);
        var ok = RegisterAgent(t1);
        var failing = RegisterAgent(t2);
        failing.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("target unreachable"));

        var result = await handlers.HandleMulticastAsync(
            Unit(), TenantId, [t1, t2], scope: null, CreateMessage(),
            reason: null, threadId: Guid.NewGuid(), CancellationToken.None);

        result.Deliveries.Count.ShouldBe(2);
        result.Deliveries.Single(d => d.Target == t1).Delivered.ShouldBeTrue();
        result.Deliveries.Single(d => d.Target == t2).Delivered.ShouldBeFalse();
        result.Deliveries.Single(d => d.Target == t2).Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task HandleMulticast_RecipientsAndScope_ThrowsValidation()
    {
        var handlers = CreateHandlers();

        var ex = await Should.ThrowAsync<MessageDeliveryException>(() =>
            handlers.HandleMulticastAsync(
                Unit(), TenantId, [Agent(ChildAgentId)], MulticastScope.UnitMembers,
                CreateMessage(), reason: null, threadId: Guid.NewGuid(), CancellationToken.None));

        ex.RejectCode.ShouldBe(MessageDeliveryException.RejectCodes.InvalidRequest);
    }

    [Fact]
    public async Task HandleMulticast_UnitMembersScope_ResolvesCallerMembers()
    {
        var handlers = CreateHandlers();
        var caller = Unit();
        var m1 = Agent(ChildAgentId);
        var m2 = Agent(OtherChildAgentId);
        RegisterAgent(m1);
        RegisterAgent(m2);
        _memberGraphStore.GetMembersAsync(UnitId, Arg.Any<CancellationToken>())
            .Returns(new[] { m1, m2 });

        var result = await handlers.HandleMulticastAsync(
            caller, TenantId, explicitRecipients: null, MulticastScope.UnitMembers,
            CreateMessage(), reason: null, threadId: Guid.NewGuid(), CancellationToken.None);

        result.Deliveries.Count.ShouldBe(2);
        result.Deliveries.Select(d => d.Target).ShouldBe(new[] { m1, m2 }, ignoreOrder: true);
    }

    [Fact]
    public async Task HandleMulticast_SiblingsScope_ResolvesParentMembersExcludingCaller()
    {
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

        var result = await handlers.HandleMulticastAsync(
            caller, TenantId, explicitRecipients: null, MulticastScope.Siblings,
            CreateMessage(), reason: null, threadId: Guid.NewGuid(), CancellationToken.None);

        result.Deliveries.Count.ShouldBe(1);
        result.Deliveries[0].Target.ShouldBe(sibling);
    }

    [Fact]
    public async Task HandleRespondTo_DeliversToConversationRosterMinusCaller_OnSameThread()
    {
        // ADR-0064 — respond_to continues the conversation a message belongs
        // to: the platform delivers to that thread's routable participants
        // minus the caller, on the SAME thread (no fork).
        var handlers = CreateHandlers();
        var caller = Unit();
        var t1 = Agent(ChildAgentId);
        var t2 = Agent(OtherChildAgentId);
        var a1 = RegisterAgent(t1);
        var a2 = RegisterAgent(t2);
        Message? d1 = null;
        Message? d2 = null;
        a1.ReceiveAsync(Arg.Do<Message>(m => d1 = m), Arg.Any<CancellationToken>()).Returns((Message?)null);
        a2.ReceiveAsync(Arg.Do<Message>(m => d2 = m), Arg.Any<CancellationToken>()).Returns((Message?)null);

        // The conversation {caller, t1, t2} the inbound message belongs to.
        var threadId = await _threadRegistry.GetOrCreateAsync(
            new[] { caller, t1, t2 }, CancellationToken.None);
        var messageId = Guid.NewGuid();
        _messageQuery.GetAsync(messageId, Arg.Any<CancellationToken>())
            .Returns(new MessageDetail(
                messageId, threadId, t1.ToString(), caller.ToString(),
                "Domain", "hi", null, DateTimeOffset.UnixEpoch));

        var result = await handlers.HandleRespondToAsync(
            caller, TenantId, messageId, CreateMessage(), reason: null, CancellationToken.None);

        // Delivered to the routable participants, the caller excluded.
        result.Deliveries.Select(d => d.Target).ShouldBe(new[] { t1, t2 }, ignoreOrder: true);
        result.Deliveries.ShouldAllBe(d => d.Delivered);
        // Same thread as the conversation — the continuation does not fork.
        result.ThreadId.ShouldBe(ParseGuid(threadId));
        d1!.ThreadId.ShouldBe(threadId);
        d2!.ThreadId.ShouldBe(threadId);
        // ADR-0066 §5: every delivered reply names the message it answers, so a
        // sender can correlate fan-out replies without an echoed token.
        d1!.InReplyTo.ShouldBe(messageId);
        d2!.InReplyTo.ShouldBe(messageId);
    }

    [Fact]
    public async Task HandleRespondTo_UnknownMessage_ThrowsInvalidRequest()
    {
        // No message → no conversation to continue. Fail before any delivery.
        var handlers = CreateHandlers();
        _messageQuery.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((MessageDetail?)null);

        var ex = await Should.ThrowAsync<MessageDeliveryException>(() =>
            handlers.HandleRespondToAsync(
                Unit(), TenantId, Guid.NewGuid(), CreateMessage(), reason: null, CancellationToken.None));

        ex.RejectCode.ShouldBe(MessageDeliveryException.RejectCodes.InvalidRequest);
    }

    [Fact]
    public async Task HandleRespondTo_ConnectorOriginThread_RefusesDeliveryToTheConnector()
    {
        // #2909 — a connector-origin thread keeps the connector in its key
        // {connector, unit} for identity, but the connector is non-routable:
        // respond_to delivers only to routable members, so a conversation
        // whose sole non-caller participant is the connector resolves zero
        // recipients. The platform refuses the reverse unit → connector
        // direction; a connector is provenance, never a deliverable target.
        var handlers = CreateHandlers();
        var caller = Unit();
        var connector = new Address(Address.ConnectorScheme, Guid.NewGuid());

        // The connector-origin conversation {connector, unit} — the connector
        // is in the thread key (used to resolve the thread here).
        var threadId = await _threadRegistry.GetOrCreateAsync(
            new[] { connector, caller }, CancellationToken.None);
        var messageId = Guid.NewGuid();
        _messageQuery.GetAsync(messageId, Arg.Any<CancellationToken>())
            .Returns(new MessageDetail(
                messageId, threadId, connector.ToString(), caller.ToString(),
                "Domain", "event", null, DateTimeOffset.UnixEpoch));

        var ex = await Should.ThrowAsync<MessageDeliveryException>(() =>
            handlers.HandleRespondToAsync(
                caller, TenantId, messageId, CreateMessage(), reason: null, CancellationToken.None));

        // Connector filtered out as non-routable → no recipient remains.
        ex.RejectCode.ShouldBe(MessageDeliveryException.RejectCodes.InvalidRequest);
    }

    private MessagingToolHandlers CreateHandlers(
        IUnitMembershipRepository? membershipRepo = null)
    {
        var services = new ServiceCollection();
        if (membershipRepo is not null)
        {
            services.AddSingleton(membershipRepo);
        }

        services.AddSingleton(Substitute.For<IUnitSubunitMembershipRepository>());
        services.AddScoped<IThreadRegistry>(_ => _threadRegistry);
        services.AddScoped<IMessageWriter>(_ => new NoOpMessageWriter());
        services.AddScoped<IMessageQueryService>(_ => _messageQuery);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var deliveryService = new MessageDeliveryService(
            _agentProxyResolver,
            _tenantResolver,
            scopeFactory,
            Substitute.For<ILogger<MessageDeliveryService>>(),
            Options.Create(new MessageDeliveryOptions
            {
                MaxAttempts = 3,
                Budget = TimeSpan.FromSeconds(2),
                InitialBackoff = TimeSpan.FromMilliseconds(1),
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

    private static Guid ParseGuid(string raw) =>
        GuidFormatter.TryParse(raw, out var g) ? g : Guid.Empty;

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
    /// No-op <see cref="IMessageWriter"/> for tests that don't assert on
    /// persistence. The handlers wire delivery through
    /// <see cref="MessageDeliveryService.DeliverWithRetryAsync"/>, which
    /// resolves a writer from each delivery's DI scope (#2764); supplying a
    /// no-op keeps the scope wiring satisfied without coupling these tests
    /// to persistence behaviour (covered by <c>MessageDeliveryServiceTests</c>).
    /// </summary>
    private sealed class NoOpMessageWriter : IMessageWriter
    {
        public Task WriteAsync(Message message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Minimal in-memory <see cref="IThreadRegistry"/>: canonicalises the
    /// participant set and reuses the same Guid for repeated lookups so
    /// tests can assert participant-set thread identity (#2596 / ADR-0030).
    /// </summary>
    private sealed class RecordingThreadRegistry : IThreadRegistry
    {
        private readonly Dictionary<string, string> _byKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<Address>> _byId = new(StringComparer.Ordinal);

        public Task<string> GetOrCreateAsync(
            IEnumerable<Address> participants, CancellationToken cancellationToken = default)
        {
            var list = participants.ToList();
            var key = string.Join('|', list
                .Select(a => $"{a.Scheme.ToLowerInvariant()}:{GuidFormatter.Format(a.Id)}")
                .OrderBy(s => s, StringComparer.Ordinal)
                .Distinct());
            if (!_byKey.TryGetValue(key, out var id))
            {
                id = GuidFormatter.Format(Guid.NewGuid());
                _byKey[key] = id;
                _byId[id] = list;
            }

            return Task.FromResult(id);
        }

        public Task<ThreadRegistryEntry?> ResolveAsync(
            string threadId, CancellationToken cancellationToken = default)
        {
            var key = GuidFormatter.TryParse(threadId, out var g)
                ? GuidFormatter.Format(g)
                : threadId;
            return Task.FromResult(_byId.TryGetValue(key, out var participants)
                ? new ThreadRegistryEntry(key, participants, DateTimeOffset.UnixEpoch)
                : null);
        }

        public Task<string> EnsureThreadAsync(
            string threadId, IEnumerable<Address> participants, CancellationToken cancellationToken = default)
        {
            var list = participants.ToList();
            var key = string.Join('|', list
                .Select(a => $"{a.Scheme.ToLowerInvariant()}:{GuidFormatter.Format(a.Id)}")
                .OrderBy(s => s, StringComparer.Ordinal)
                .Distinct());
            if (!_byKey.TryGetValue(key, out var id))
            {
                id = GuidFormatter.TryParse(threadId, out var supplied)
                    ? GuidFormatter.Format(supplied)
                    : GuidFormatter.Format(Guid.NewGuid());
                _byKey[key] = id;
                _byId[id] = list;
            }

            return Task.FromResult(id);
        }
    }
}
