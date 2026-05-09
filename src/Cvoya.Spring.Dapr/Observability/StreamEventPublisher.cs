// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using System.Text.Json;

using Cvoya.Spring.Core.Execution;

using global::Dapr.Client;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Publishes <see cref="StreamEvent"/> instances to a Dapr pub/sub topic
/// for real-time observation of agent execution.
/// </summary>
public class StreamEventPublisher(
    DaprClient daprClient,
    IOptions<StreamEventPublisherOptions> options,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<StreamEventPublisher>();
    private readonly StreamEventPublisherOptions _options = options.Value;

    /// <summary>
    /// Publishes a stream event to the agent's pub/sub topic.
    /// Topic format: <c>agent/{agentId}/stream</c>.
    /// </summary>
    /// <param name="agentId">The agent identifier.</param>
    /// <param name="streamEvent">The stream event to publish.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous publish operation.</returns>
    public async Task PublishAsync(string agentId, StreamEvent streamEvent, CancellationToken cancellationToken = default)
    {
        var topicName = $"agent/{agentId}/stream";

        _logger.LogDebug("Publishing {EventType} to topic {TopicName} for agent {AgentId}.",
            streamEvent.GetType().Name, topicName, agentId);

        var envelope = new StreamEventEnvelope
        {
            AgentId = agentId,
            EventType = streamEvent.GetType().Name,
            Timestamp = streamEvent.Timestamp,
            Payload = JsonSerializer.SerializeToElement(streamEvent, streamEvent.GetType())
        };

        await daprClient.PublishEventAsync(
            _options.PubSubName,
            topicName,
            envelope,
            cancellationToken);
    }
}

/// <summary>
/// Envelope wrapping a <see cref="StreamEvent"/> for pub/sub transport.
/// </summary>
public class StreamEventEnvelope
{
    /// <summary>
    /// Gets or sets the agent identifier.
    /// </summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the event type discriminator.
    /// </summary>
    public string EventType { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the event timestamp.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the serialized stream event payload.
    /// </summary>
    public JsonElement Payload { get; set; }
}

/// <summary>
/// Configuration options for the <see cref="StreamEventPublisher"/>.
/// </summary>
public class StreamEventPublisherOptions
{
    /// <summary>
    /// The configuration section name.
    /// </summary>
    public const string SectionName = "StreamEventPublisher";

    /// <summary>
    /// Gets or sets the Dapr pub/sub component name.
    /// </summary>
    public string PubSubName { get; set; } = "pubsub";
}
