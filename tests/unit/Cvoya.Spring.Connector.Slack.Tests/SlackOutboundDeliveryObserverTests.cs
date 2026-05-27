// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Tests;

using System.Text.Json;

using Cvoya.Spring.Connector.Slack.Outbound;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit coverage for <see cref="SlackOutboundDeliveryObserver"/>, the
/// adapter that bridges the platform's per-delivery
/// <c>IConnectorDeliveryObserver</c> seam onto the Slack outbound
/// dispatcher. The observer should:
/// <list type="bullet">
///   <item><description>Forward every Domain delivery to the dispatcher.</description></item>
///   <item><description>Skip control messages (HealthCheck / Cancel / StatusQuery / …).</description></item>
///   <item><description>Swallow the <see cref="NotSupportedException"/> the dispatcher throws on the PrivateChannel branch (ADR-0061 §7.2) so the platform's delivery loop is not poisoned by a connector-side limitation.</description></item>
/// </list>
/// </summary>
public class SlackOutboundDeliveryObserverTests
{
    private static readonly Address Caller = new(Address.AgentScheme, new Guid("00000001-0000-0000-0000-000000000000"));
    private static readonly Address Target = new(Address.HumanScheme, new Guid("11111111-0000-0000-0000-000000000000"));

    private static Message MakeMessage(MessageType type = MessageType.Domain)
    {
        return new Message(
            Id: Guid.NewGuid(),
            From: Caller,
            To: Target,
            Type: type,
            ThreadId: Guid.NewGuid().ToString("N"),
            Payload: JsonDocument.Parse("\"hello\"").RootElement.Clone(),
            Timestamp: DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task OnDelivered_DomainMessage_InvokesDispatcher()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = Substitute.For<ISlackOutboundDispatcher>();
        dispatcher
            .DispatchAsync(Arg.Any<Message>(), Arg.Any<IReadOnlyList<Address>>(), Arg.Any<CancellationToken>())
            .Returns(SlackOutboundResult.Delivered);

        var observer = new SlackOutboundDeliveryObserver(dispatcher, NullLoggerFactory.Instance);
        var message = MakeMessage();
        var participants = new[] { Caller, Target };

        await observer.OnDeliveredAsync(Caller, Target, message, participants, ct);

        await dispatcher.Received(1).DispatchAsync(
            Arg.Is<Message>(m => m.Id == message.Id),
            Arg.Is<IReadOnlyList<Address>>(p => p.Count == 2),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnDelivered_ControlMessage_DoesNotInvokeDispatcher()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = Substitute.For<ISlackOutboundDispatcher>();

        var observer = new SlackOutboundDeliveryObserver(dispatcher, NullLoggerFactory.Instance);
        var message = MakeMessage(type: MessageType.HealthCheck);
        var participants = new[] { Caller, Target };

        await observer.OnDeliveredAsync(Caller, Target, message, participants, ct);

        await dispatcher.DidNotReceive().DispatchAsync(
            Arg.Any<Message>(),
            Arg.Any<IReadOnlyList<Address>>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnDelivered_DispatcherReturnsNoSlackSurface_DoesNotThrow()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = Substitute.For<ISlackOutboundDispatcher>();
        dispatcher
            .DispatchAsync(Arg.Any<Message>(), Arg.Any<IReadOnlyList<Address>>(), Arg.Any<CancellationToken>())
            .Returns(SlackOutboundResult.NoSlackSurface);

        var observer = new SlackOutboundDeliveryObserver(dispatcher, NullLoggerFactory.Instance);
        var message = MakeMessage();
        var participants = new[] { Caller, Target };

        await observer.OnDeliveredAsync(Caller, Target, message, participants, ct);
    }

    [Fact]
    public async Task OnDelivered_DispatcherThrowsNotSupported_Swallows()
    {
        // ADR-0061 §7.2 — the PrivateChannel branch is reserved for the
        // hybrid mode and throws NotSupportedException in v0.1. The observer
        // must swallow it so the platform's delivery hot path is not
        // affected by a connector-side limitation.
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = Substitute.For<ISlackOutboundDispatcher>();
        dispatcher
            .DispatchAsync(Arg.Any<Message>(), Arg.Any<IReadOnlyList<Address>>(), Arg.Any<CancellationToken>())
            .Returns<Task<SlackOutboundResult>>(_ => throw new NotSupportedException("hybrid mode"));

        var observer = new SlackOutboundDeliveryObserver(dispatcher, NullLoggerFactory.Instance);
        var message = MakeMessage();
        var participants = new[] { Caller, Target };

        await Should.NotThrowAsync(async () =>
            await observer.OnDeliveredAsync(Caller, Target, message, participants, ct));
    }

    [Fact]
    public async Task OnDelivered_PassesParticipantsThrough()
    {
        var ct = TestContext.Current.CancellationToken;
        var dispatcher = Substitute.For<ISlackOutboundDispatcher>();
        dispatcher
            .DispatchAsync(Arg.Any<Message>(), Arg.Any<IReadOnlyList<Address>>(), Arg.Any<CancellationToken>())
            .Returns(SlackOutboundResult.Delivered);

        var observer = new SlackOutboundDeliveryObserver(dispatcher, NullLoggerFactory.Instance);
        var message = MakeMessage();

        // Shared-thread send: participant set has caller + multiple recipients.
        var thirdParty = new Address(Address.AgentScheme, new Guid("00000002-0000-0000-0000-000000000000"));
        var participants = new[] { Caller, Target, thirdParty };

        await observer.OnDeliveredAsync(Caller, Target, message, participants, ct);

        await dispatcher.Received(1).DispatchAsync(
            Arg.Any<Message>(),
            Arg.Is<IReadOnlyList<Address>>(p => p.Count == 3 && p.Contains(thirdParty)),
            Arg.Any<CancellationToken>());
    }
}
