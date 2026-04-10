// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using System.Text.Json;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Messaging;
using global::Dapr.Actors.Runtime;
using Microsoft.Extensions.Logging;

/// <summary>
/// Dapr virtual actor representing a connector in the Spring Voyage platform.
/// Connectors bridge external systems (e.g., GitHub, Slack) into the platform,
/// translating external events into domain messages and vice versa.
/// This is a shell implementation; event translation logic is future work.
/// </summary>
public class ConnectorActor(ActorHost host, ILoggerFactory loggerFactory) : Actor(host), IConnectorActor
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ConnectorActor>();

    /// <summary>
    /// Gets the address of this connector actor.
    /// </summary>
    public Address Address => new("connector", Id.GetId());

    /// <inheritdoc />
    public async Task<Message?> ReceiveAsync(Message message, CancellationToken cancellationToken = default)
    {
        try
        {
            return message.Type switch
            {
                MessageType.StatusQuery => await HandleStatusQueryAsync(message, cancellationToken),
                MessageType.HealthCheck => HandleHealthCheck(message),
                MessageType.Domain => await HandleDomainMessageAsync(message, cancellationToken),
                _ => throw new SpringException($"Unknown message type: {message.Type}")
            };
        }
        catch (Exception ex) when (ex is not SpringException)
        {
            _logger.LogError(ex, "Unhandled exception processing message {MessageId} of type {MessageType} in connector {ActorId}",
                message.Id, message.Type, Id.GetId());
            return CreateErrorResponse(message, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<ConnectionStatus> GetConnectionStatusAsync(CancellationToken cancellationToken = default)
    {
        var result = await StateManager
            .TryGetStateAsync<ConnectionStatus>(StateKeys.ConnectorStatus, cancellationToken)
            ;

        return result.HasValue ? result.Value : ConnectionStatus.Disconnected;
    }

    /// <summary>
    /// Sets the connection status of this connector.
    /// </summary>
    /// <param name="status">The new connection status.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SetConnectionStatusAsync(ConnectionStatus status, CancellationToken cancellationToken = default)
    {
        await StateManager
            .SetStateAsync(StateKeys.ConnectorStatus, status, cancellationToken)
            ;

        _logger.LogInformation("Connector {ActorId} status changed to {Status}", Id.GetId(), status);
    }

    /// <summary>
    /// Handles a status query message by returning the current connection status and connector type.
    /// </summary>
    private async Task<Message?> HandleStatusQueryAsync(Message message, CancellationToken cancellationToken)
    {
        var status = await GetConnectionStatusAsync(cancellationToken);
        var connectorType = await StateManager
            .TryGetStateAsync<string>(StateKeys.ConnectorType, cancellationToken)
            ;

        var statusPayload = JsonSerializer.SerializeToElement(new
        {
            Status = status.ToString(),
            ConnectorType = connectorType.HasValue ? connectorType.Value : "unknown"
        });

        return new Message(
            Guid.NewGuid(),
            Address,
            message.From,
            MessageType.StatusQuery,
            message.ConversationId,
            statusPayload,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Handles a health check message by returning an acknowledgment indicating the actor is alive.
    /// </summary>
    private Message HandleHealthCheck(Message message)
    {
        var healthPayload = JsonSerializer.SerializeToElement(new { Healthy = true });

        return new Message(
            Guid.NewGuid(),
            Address,
            message.From,
            MessageType.HealthCheck,
            message.ConversationId,
            healthPayload,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Handles a domain message. This is a skeleton handler that logs the message
    /// and returns an acknowledgment. Event translation is future work.
    /// </summary>
    private async Task<Message?> HandleDomainMessageAsync(Message message, CancellationToken cancellationToken)
    {
        _ = cancellationToken; // Unused but kept for signature consistency.

        _logger.LogInformation("Connector {ActorId} received domain message {MessageId}; event translation is not yet implemented",
            Id.GetId(), message.Id);

        return await Task.FromResult<Message?>(CreateAckResponse(message));
    }

    /// <summary>
    /// Creates an acknowledgment response message.
    /// </summary>
    private Message CreateAckResponse(Message originalMessage)
    {
        var ackPayload = JsonSerializer.SerializeToElement(new { Acknowledged = true });
        return new Message(
            Guid.NewGuid(),
            Address,
            originalMessage.From,
            MessageType.Domain,
            originalMessage.ConversationId,
            ackPayload,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Creates an error response message.
    /// </summary>
    private Message CreateErrorResponse(Message originalMessage, string errorMessage)
    {
        var errorPayload = JsonSerializer.SerializeToElement(new { Error = errorMessage });
        return new Message(
            Guid.NewGuid(),
            Address,
            originalMessage.From,
            MessageType.Domain,
            originalMessage.ConversationId,
            errorPayload,
            DateTimeOffset.UtcNow);
    }
}
