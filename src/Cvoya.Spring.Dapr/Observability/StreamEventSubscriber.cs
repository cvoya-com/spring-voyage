// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging;

/// <summary>
/// Subscribes to stream event envelopes from Dapr pub/sub and projects them
/// into the <see cref="IActivityEventBus"/> as <see cref="ActivityEvent"/> instances.
/// This bridges execution-level streaming events to the activity observation layer.
/// </summary>
public class StreamEventSubscriber(
    IActivityEventBus activityEventBus,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<StreamEventSubscriber>();

    /// <summary>
    /// Handles a stream event envelope received from Dapr pub/sub,
    /// converting it to an <see cref="ActivityEvent"/> and publishing it to the bus.
    /// </summary>
    /// <param name="envelope">The stream event envelope from pub/sub.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task HandleAsync(StreamEventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Received {EventType} for agent {AgentId}.",
            envelope.EventType, envelope.AgentId);

        var description = BuildDescription(envelope);
        var activityEventType = MapToActivityEventType(envelope.EventType);
        var severity = ResolveSeverity(envelope);
        var correlationId = TryGetOptionalText(envelope.Payload, "CallId");

        var activityEvent = new ActivityEvent(
            Guid.NewGuid(),
            envelope.Timestamp,
            new Address("agent", envelope.AgentId),
            activityEventType,
            severity,
            description,
            envelope.Payload,
            CorrelationId: correlationId);

        await activityEventBus.PublishAsync(activityEvent, cancellationToken);
    }

    private static ActivitySeverity ResolveSeverity(StreamEventEnvelope envelope)
    {
        // Tool failures escalate to Warning so Rx alert pipelines
        // (Where(e => e.Severity >= Warning)) can flag them without having
        // to parse Details. Everything else flows at Info / Debug granularity.
        if (envelope.EventType == nameof(StreamEvent.ToolResult) &&
            envelope.Payload.TryGetProperty("IsError", out var errProp) &&
            errProp.ValueKind == JsonValueKind.True)
        {
            return ActivitySeverity.Warning;
        }

        return envelope.EventType switch
        {
            nameof(StreamEvent.TokenDelta) => ActivitySeverity.Debug,
            _ => ActivitySeverity.Info,
        };
    }

    private static string BuildDescription(StreamEventEnvelope envelope)
    {
        return envelope.EventType switch
        {
            nameof(StreamEvent.TokenDelta) => TryGetText(envelope.Payload, "Text", "Token generated"),
            nameof(StreamEvent.ThinkingDelta) => "Thinking...",
            nameof(StreamEvent.Checkpoint) => "Checkpoint saved",
            nameof(StreamEvent.Completed) => "Execution completed",
            nameof(StreamEvent.ToolCall) => $"Tool call: {TryGetText(envelope.Payload, "ToolName", "tool")}",
            nameof(StreamEvent.ToolResult) => BuildToolResultDescription(envelope.Payload),
            _ => $"Stream event: {envelope.EventType}"
        };
    }

    private static ActivityEventType MapToActivityEventType(string eventType)
    {
        return eventType switch
        {
            nameof(StreamEvent.TokenDelta) => ActivityEventType.TokenDelta,
            nameof(StreamEvent.Completed) => ActivityEventType.ConversationCompleted,
            nameof(StreamEvent.ToolCall) => ActivityEventType.ToolCall,
            nameof(StreamEvent.ToolResult) => ActivityEventType.ToolResult,
            _ => ActivityEventType.StateChanged
        };
    }

    private static string BuildToolResultDescription(JsonElement payload)
    {
        var name = TryGetText(payload, "ToolName", "tool");
        var isError = payload.TryGetProperty("IsError", out var errProp)
            && errProp.ValueKind == JsonValueKind.True;
        return isError ? $"Tool result (error): {name}" : $"Tool result: {name}";
    }

    private static string TryGetText(JsonElement payload, string propertyName, string fallback)
    {
        if (payload.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? fallback;
        }

        return fallback;
    }

    private static string? TryGetOptionalText(JsonElement payload, string propertyName)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty(propertyName, out var value) &&
            value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        return null;
    }
}