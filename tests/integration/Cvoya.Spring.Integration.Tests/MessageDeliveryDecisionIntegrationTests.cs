// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
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
/// Integration smoke tests for the ADR-0048 / ADR-0049 messaging tools.
/// <c>sv.messaging.send</c> / <c>sv.messaging.multicast</c> are one-way
/// delivery tools: they durably enqueue the message and emit a plain
/// <see cref="ActivityEventType.MessageSent"/> activity — never a
/// <c>DecisionMade</c> event. Child mailboxes, the hop actor, and the
/// activity bus are mocked boundaries.
/// </summary>
public class MessageDeliveryDecisionIntegrationTests
{
    private static readonly Address ParentUnit =
        new(Address.UnitScheme, new Guid("bbbbbbbb-0000-0000-0000-000000001828"));

    private static readonly Guid TenantId = OssTenantIds.Default;

    private static readonly Address ChildOne =
        new(Address.AgentScheme, new Guid("aaaaaaaa-0000-0000-0000-000000001828"));

    private static readonly Address ChildTwo =
        new(Address.AgentScheme, new Guid("aaaaaaaa-0000-0000-0000-000000001829"));

    private static readonly Address ChildThree =
        new(Address.AgentScheme, new Guid("aaaaaaaa-0000-0000-0000-000000001830"));

    [Fact]
    public async Task HandleSend_DeliversAndEmitsMessageSentEvent()
    {
        var harness = CreateHarness();
        var child = Substitute.For<IAgent>();
        var message = CreateMessage(ParentUnit);
        var upstreamThread = Guid.NewGuid();

        harness.RegisterAgent(ChildOne, child);
        child.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);

        var result = await harness.Handlers.HandleSendAsync(
            ParentUnit,
            TenantId,
            [ChildOne],
            scope: null,
            message,
            reason: "route to the specialist",
            upstreamThread,
            TestContext.Current.CancellationToken);

        // ADR-0049 — send returns a per-recipient delivery acknowledgement
        // on the shared thread for {caller, recipients}.
        result.Deliveries.Count.ShouldBe(1);
        result.Deliveries[0].Delivered.ShouldBeTrue();
        result.Deliveries[0].Target.ShouldBe(ChildOne);
        result.MessageId.ShouldBe(message.Id);

        var activity = harness.ReadSingleActivity();
        activity.EventType.ShouldBe(ActivityEventType.MessageSent);
        activity.Source.ShouldBe(ParentUnit);
        activity.CorrelationId.ShouldBe(upstreamThread.ToString("D"));
    }

    [Fact]
    public async Task HandleSend_DeliveryFails_ReportsFailedRecipient()
    {
        // A persistent transient ReceiveAsync failure (ADR-0049 §6). After
        // #2747 a single failed delivery surfaces as that recipient's
        // outcome, not a thrown exception, so a partial-success multi-
        // recipient send still acks the rest.
        var harness = CreateHarness();
        var child = Substitute.For<IAgent>();
        var message = CreateMessage(ParentUnit);
        const string failureDescription = "child unreachable";

        harness.RegisterAgent(ChildOne, child);
        child.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException(failureDescription));

        var result = await harness.Handlers.HandleSendAsync(
            ParentUnit,
            TenantId,
            [ChildOne],
            scope: null,
            message,
            reason: failureDescription,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        result.Deliveries.Count.ShouldBe(1);
        result.Deliveries[0].Delivered.ShouldBeFalse();
        result.Deliveries[0].Error.ShouldNotBeNull();
    }

    [Fact]
    public async Task HandleMulticast_AllTargetsSucceed_EmitsMessageSentEvent()
    {
        var harness = CreateHarness();
        var childOne = Substitute.For<IAgent>();
        var childTwo = Substitute.For<IAgent>();
        var childThree = Substitute.For<IAgent>();
        var message = CreateMessage(ParentUnit);

        harness.RegisterAgent(ChildOne, childOne);
        harness.RegisterAgent(ChildTwo, childTwo);
        harness.RegisterAgent(ChildThree, childThree);
        childOne.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>()).Returns((Message?)null);
        childTwo.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>()).Returns((Message?)null);
        childThree.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>()).Returns((Message?)null);

        var result = await harness.Handlers.HandleMulticastAsync(
            ParentUnit,
            TenantId,
            [ChildOne, ChildTwo, ChildThree],
            scope: null,
            message,
            reason: "ask all children",
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        result.Deliveries.Select(o => o.Target)
            .ShouldBe([ChildOne, ChildTwo, ChildThree]);
        result.Deliveries.All(o => o.Delivered).ShouldBeTrue();

        harness.ReadSingleActivity().EventType.ShouldBe(ActivityEventType.MessageSent);
    }

    [Fact]
    public async Task HandleMulticast_OneTargetDeliveryFails_ReportsPerTargetOutcome()
    {
        var harness = CreateHarness();
        var childOne = Substitute.For<IAgent>();
        var childTwo = Substitute.For<IAgent>();
        var childThree = Substitute.For<IAgent>();
        var message = CreateMessage(ParentUnit);
        const string failureDescription = "child two unreachable";

        harness.RegisterAgent(ChildOne, childOne);
        harness.RegisterAgent(ChildTwo, childTwo);
        harness.RegisterAgent(ChildThree, childThree);
        childOne.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>()).Returns((Message?)null);
        childTwo.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException(failureDescription));
        childThree.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>()).Returns((Message?)null);

        var result = await harness.Handlers.HandleMulticastAsync(
            ParentUnit,
            TenantId,
            [ChildOne, ChildTwo, ChildThree],
            scope: null,
            message,
            reason: failureDescription,
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        result.Deliveries.Count.ShouldBe(3);
        result.Deliveries[0].Delivered.ShouldBeTrue();
        result.Deliveries[1].Target.ShouldBe(ChildTwo);
        result.Deliveries[1].Delivered.ShouldBeFalse();
        result.Deliveries[1].Error.ShouldNotBeNull();
        result.Deliveries[2].Delivered.ShouldBeTrue();
    }

    private static HandlerHarness CreateHarness()
    {
        var agents = new Dictionary<string, IAgent>(StringComparer.OrdinalIgnoreCase);
        var agentProxyResolver = Substitute.For<IAgentProxyResolver>();
        var activityEventBus = Substitute.For<IActivityEventBus>();
        var publishedEvents = new List<ActivityEvent>();

        agentProxyResolver.Resolve(Arg.Any<string>(), Arg.Any<string>())
            .Returns(call =>
            {
                var scheme = call.ArgAt<string>(0);
                var actorId = call.ArgAt<string>(1);
                return agents.TryGetValue(Key(scheme, actorId), out var agent)
                    ? agent
                    : null;
            });

        activityEventBus
            .PublishAsync(
                Arg.Do<ActivityEvent>(activityEvent => publishedEvents.Add(activityEvent)),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // ADR-0049 — tighten the delivery retry budget so the
        // terminal-failure path exhausts in milliseconds under test.
        var deliveryOptions = Options.Create(new MessageDeliveryOptions
        {
            MaxAttempts = 3,
            Budget = TimeSpan.FromSeconds(2),
            InitialBackoff = TimeSpan.FromMilliseconds(1),
        });

        var scopeFactory = new ThreadRegistryServiceScopeFactory();

        var deliveryService = new MessageDeliveryService(
            agentProxyResolver,
            new SingleTenantMessageTenantResolver(),
            scopeFactory,
            Substitute.For<ILogger<MessageDeliveryService>>(),
            deliveryOptions);

        var handlers = new MessagingToolHandlers(
            deliveryService,
            Substitute.For<IUnitMemberGraphStore>(),
            scopeFactory,
            activityEventBus,
            Substitute.For<ILogger<MessagingToolHandlers>>());

        return new HandlerHarness(handlers, agents, publishedEvents);
    }

    private static string Key(Address address) => Key(address.Scheme, GuidFormatter.Format(address.Id));

    private static string Key(string scheme, string id) => $"{scheme}:{id}";

    private static Message CreateMessage(Address to) =>
        new(
            Guid.NewGuid(),
            new Address(Address.HumanScheme, new Guid("cccccccc-0000-0000-0000-000000001828")),
            to,
            MessageType.Domain,
            Guid.NewGuid().ToString("D"),
            JsonSerializer.SerializeToElement(new { Content = "work" }),
            DateTimeOffset.UtcNow);

    private sealed record HandlerHarness(
        MessagingToolHandlers Handlers,
        Dictionary<string, IAgent> Agents,
        List<ActivityEvent> PublishedEvents)
    {
        public void RegisterAgent(Address address, IAgent agent) =>
            Agents[Key(address)] = agent;

        public ActivityEvent ReadSingleActivity()
        {
            PublishedEvents.Count.ShouldBe(1);
            return PublishedEvents.Single();
        }
    }

    /// <summary>
    /// A scope factory whose scopes resolve an in-memory
    /// <see cref="IThreadRegistry"/> and a no-op
    /// <see cref="IMessageWriter"/> — the explicit-address multicast / send
    /// paths exercised here never resolve the membership repositories, but
    /// the delivery seam re-resolves a per-hop thread (#2596) and writes the
    /// outbound envelope to the messages table (#2764).
    /// </summary>
    private sealed class ThreadRegistryServiceScopeFactory
        : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        private readonly RecordingThreadRegistry _threadRegistry = new();
        private readonly NoOpMessageWriter _messageWriter = new();

        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IThreadRegistry))
            {
                return _threadRegistry;
            }

            if (serviceType == typeof(IMessageWriter))
            {
                return _messageWriter;
            }

            return null;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// No-op <see cref="IMessageWriter"/> for delivery tests that don't
    /// assert on persistence (#2764). Coverage for the persist-before-deliver
    /// invariant lives in <c>MessageDeliveryServiceTests</c>.
    /// </summary>
    private sealed class NoOpMessageWriter : IMessageWriter
    {
        public Task WriteAsync(Message message, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    /// <summary>
    /// Minimal in-memory <see cref="IThreadRegistry"/>: canonicalises the
    /// participant set and reuses the same Guid for repeated lookups so the
    /// delivery seam resolves a stable per-hop thread (#2596 / ADR-0030).
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
