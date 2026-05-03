// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Observability;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="StreamEventSubscriber"/>.
/// </summary>
public class StreamEventSubscriberTests
{
    // Stable Guid hex used as AgentId on every envelope; matches what the
    // production code passes through Address.For("agent", AgentId) post-#1629.
    private static readonly string AgentHex = TestSlugIds.HexFor("agent-1");
    private static readonly string MyAgentHex = TestSlugIds.HexFor("my-agent-id");

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
            EventType = nameof(StreamEvent.TokenDelta),
            AgentId = AgentHex,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(tokenDelta)
        };

        await _subscriber.HandleAsync(envelope, TestContext.Current.CancellationToken);

        await _activityEventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.Source.Path == AgentHex &&
                e.EventType == ActivityEventType.TokenDelta &&
                e.Summary == "Hello"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_Completed_PublishesThreadCompletedEvent()
    {
        var completed = new StreamEvent.Completed(
            Guid.NewGuid(), DateTimeOffset.UtcNow, 100, 50, "end_turn");
        var envelope = new StreamEventEnvelope
        {
            EventType = nameof(StreamEvent.Completed),
            AgentId = AgentHex,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(completed)
        };

        await _subscriber.HandleAsync(envelope, TestContext.Current.CancellationToken);

        await _activityEventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ThreadCompleted &&
                e.Summary == "Execution completed"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_PreservesAgentAddress()
    {
        var tokenDelta = new StreamEvent.TokenDelta(Guid.NewGuid(), DateTimeOffset.UtcNow, "test");
        var envelope = new StreamEventEnvelope
        {
            EventType = nameof(StreamEvent.TokenDelta),
            AgentId = MyAgentHex,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(tokenDelta)
        };

        await _subscriber.HandleAsync(envelope, TestContext.Current.CancellationToken);

        await _activityEventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.Source.Scheme == "agent" &&
                e.Source.Path == MyAgentHex),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ToolCall_PublishesActivityEventWithCallIdCorrelation()
    {
        var call = new StreamEvent.ToolCall(
            Guid.NewGuid(), DateTimeOffset.UtcNow,
            CallId: "call-abc",
            ToolName: "github.create_pr",
            Arguments: "{}");
        var envelope = new StreamEventEnvelope
        {
            EventType = nameof(StreamEvent.ToolCall),
            AgentId = AgentHex,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(call)
        };

        await _subscriber.HandleAsync(envelope, TestContext.Current.CancellationToken);

        await _activityEventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ToolCall &&
                e.Summary == "Tool call: github.create_pr" &&
                e.CorrelationId == "call-abc"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ToolResult_Success_PublishesInfoActivityEvent()
    {
        var result = new StreamEvent.ToolResult(
            Guid.NewGuid(), DateTimeOffset.UtcNow,
            CallId: "call-abc",
            ToolName: "github.create_pr",
            Result: "{\"url\":\"...\"}",
            IsError: false);
        var envelope = new StreamEventEnvelope
        {
            EventType = nameof(StreamEvent.ToolResult),
            AgentId = AgentHex,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(result)
        };

        await _subscriber.HandleAsync(envelope, TestContext.Current.CancellationToken);

        await _activityEventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ToolResult &&
                e.Severity == ActivitySeverity.Info &&
                e.CorrelationId == "call-abc" &&
                e.Summary == "Tool result: github.create_pr"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HandleAsync_ToolResult_Error_EscalatesSeverityToWarning()
    {
        var result = new StreamEvent.ToolResult(
            Guid.NewGuid(), DateTimeOffset.UtcNow,
            CallId: "call-xyz",
            ToolName: "github.create_pr",
            Result: "rate limited",
            IsError: true);
        var envelope = new StreamEventEnvelope
        {
            EventType = nameof(StreamEvent.ToolResult),
            AgentId = AgentHex,
            Timestamp = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.SerializeToElement(result)
        };

        await _subscriber.HandleAsync(envelope, TestContext.Current.CancellationToken);

        await _activityEventBus.Received(1).PublishAsync(
            Arg.Is<ActivityEvent>(e =>
                e.EventType == ActivityEventType.ToolResult &&
                e.Severity == ActivitySeverity.Warning &&
                e.Summary == "Tool result (error): github.create_pr"),
            Arg.Any<CancellationToken>());
    }
}