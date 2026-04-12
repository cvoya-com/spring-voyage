// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Observability;

using FluentAssertions;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Xunit;

/// <summary>
/// Tests for <see cref="StreamEventSubscriber"/>.
/// </summary>
public class StreamEventSubscriberTests
{
    private readonly IActivityEventBus _activityEventBus = Substitute.For<IActivityEventBus>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly StreamEventSubscriber _subscriber;

    public StreamEventSubscriberTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _subscriber = new StreamEventSubscriber(_activityEventBus, _loggerFactory);
    }

    [Fact]
    public async Task HandleAsync_TokenDelta_PublishesActivityEvent()
    {
        var tokenDelta = new StreamEvent.TokenDelta(Guid.NewGuid(), DateTimeOffset.UtcNow, "Hello");
        var envelope = new StreamEventEnvelope
        {
            AgentId = "agent-1",
            EventType = nameof(StreamEvent.TokenDelta),
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(tokenDelta)
        };

        await _subscriber.HandleAsync(envelope, TestContext.Current.CancellationToken);

        await _activityEventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.Source.Path == "agent-1" &&
                e.EventType == ActivityEventType.TokenDelta &&
                e.Summary == "Hello"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_Completed_PublishesConversationCompletedEvent()
    {
        var completed = new StreamEvent.Completed(
            Guid.NewGuid(), DateTimeOffset.UtcNow, 100, 50, "end_turn");
        var envelope = new StreamEventEnvelope
        {
            AgentId = "agent-3",
            EventType = nameof(StreamEvent.Completed),
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(completed)
        };

        await _subscriber.HandleAsync(envelope, TestContext.Current.CancellationToken);

        await _activityEventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ConversationCompleted &&
                e.Summary == "Execution completed"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_PreservesAgentAddress()
    {
        var tokenDelta = new StreamEvent.TokenDelta(Guid.NewGuid(), DateTimeOffset.UtcNow, "test");
        var envelope = new StreamEventEnvelope
        {
            AgentId = "my-agent-id",
            EventType = nameof(StreamEvent.TokenDelta),
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(tokenDelta)
        };

        await _subscriber.HandleAsync(envelope, TestContext.Current.CancellationToken);

        await _activityEventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.Source.Scheme == "agent" &&
                e.Source.Path == "my-agent-id"),
            Arg.Any<CancellationToken>());
    }
}