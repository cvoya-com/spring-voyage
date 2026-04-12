// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Observability;

using FluentAssertions;

using global::Dapr.Client;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Xunit;

/// <summary>
/// Tests for <see cref="StreamEventPublisher"/>.
/// </summary>
public class StreamEventPublisherTests
{
    private readonly DaprClient _daprClient = Substitute.For<DaprClient>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly StreamEventPublisher _publisher;

    public StreamEventPublisherTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        var options = Options.Create(new StreamEventPublisherOptions { PubSubName = "test-pubsub" });
        _publisher = new StreamEventPublisher(_daprClient, options, _loggerFactory);
    }

    [Fact]
    public async Task PublishAsync_TokenDelta_PublishesToCorrectTopic()
    {
        var streamEvent = new StreamEvent.TokenDelta(Guid.NewGuid(), DateTimeOffset.UtcNow, "Hello");

        await _publisher.PublishAsync("agent-1", streamEvent, TestContext.Current.CancellationToken);

        await _daprClient.Received(1).PublishEventAsync(
            "test-pubsub",
            "agent/agent-1/stream",
            Arg.Is<StreamEventEnvelope>(e =>
                e.AgentId == "agent-1" &&
                e.EventType == "TokenDelta"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_Completed_PublishesCompletedEventType()
    {
        var streamEvent = new StreamEvent.Completed(Guid.NewGuid(), DateTimeOffset.UtcNow, 100, 50, "end_turn");

        await _publisher.PublishAsync("agent-2", streamEvent, TestContext.Current.CancellationToken);

        await _daprClient.Received(1).PublishEventAsync(
            "test-pubsub",
            "agent/agent-2/stream",
            Arg.Is<StreamEventEnvelope>(e =>
                e.AgentId == "agent-2" &&
                e.EventType == "Completed"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PublishAsync_ThinkingDelta_IncludesCorrectPayload()
    {
        var streamEvent = new StreamEvent.ThinkingDelta(
            Guid.NewGuid(), DateTimeOffset.UtcNow, "Considering options...");

        await _publisher.PublishAsync("agent-3", streamEvent, TestContext.Current.CancellationToken);

        await _daprClient.Received(1).PublishEventAsync(
            "test-pubsub",
            "agent/agent-3/stream",
            Arg.Is<StreamEventEnvelope>(e =>
                e.EventType == "ThinkingDelta" &&
                e.Payload.GetProperty("Text").GetString() == "Considering options..."),
            Arg.Any<CancellationToken>());
    }
}