// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Messaging;

using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

/// <summary>
/// Dapr virtual actor representing a human user in the Spring Voyage platform.
/// Humans have identity, permission levels, and notification preferences.
/// Domain messages are rejected for viewers; all other permission levels receive
/// an acknowledgment (notification routing is future work).
/// </summary>
public class HumanActor(ActorHost host, ILoggerFactory loggerFactory) : Actor(host), IHumanActor
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<HumanActor>();

    /// <summary>
    /// Gets the address of this human actor.
    /// </summary>
    public Address Address => new("human", Id.GetId());

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
            _logger.LogError(ex, "Unhandled exception processing message {MessageId} of type {MessageType} in human actor {ActorId}",
                message.Id, message.Type, Id.GetId());
            return CreateErrorResponse(message, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task<PermissionLevel> GetPermissionAsync(CancellationToken cancellationToken = default)
    {
        var result = await StateManager
            .TryGetStateAsync<PermissionLevel>(StateKeys.HumanPermission, cancellationToken)
            ;

        return result.HasValue ? result.Value : PermissionLevel.Viewer;
    }

    /// <summary>
    /// Sets the permission level for this human actor.
    /// </summary>
    /// <param name="level">The new permission level.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    public async Task SetPermissionAsync(PermissionLevel level, CancellationToken cancellationToken = default)
    {
        await StateManager
            .SetStateAsync(StateKeys.HumanPermission, level, cancellationToken)
            ;

        _logger.LogInformation("Human actor {ActorId} permission changed to {Permission}", Id.GetId(), level);
    }

    /// <summary>
    /// Handles a status query message by returning the current permission level and identity.
    /// </summary>
    private async Task<Message?> HandleStatusQueryAsync(Message message, CancellationToken cancellationToken)
    {
        var permission = await GetPermissionAsync(cancellationToken);
        var identity = await StateManager
            .TryGetStateAsync<string>(StateKeys.HumanIdentity, cancellationToken)
            ;

        var statusPayload = JsonSerializer.SerializeToElement(new
        {
            Permission = permission.ToString(),
            Identity = identity.HasValue ? identity.Value : "unknown"
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
    /// Handles a domain message by checking permission level.
    /// Viewers are rejected; Operators and Owners receive an acknowledgment.
    /// Notification channel routing is future work.
    /// </summary>
    private async Task<Message?> HandleDomainMessageAsync(Message message, CancellationToken cancellationToken)
    {
        var permission = await GetPermissionAsync(cancellationToken);

        if (permission == PermissionLevel.Viewer)
        {
            _logger.LogWarning("Human actor {ActorId} rejected domain message {MessageId}: insufficient permission (Viewer)",
                Id.GetId(), message.Id);
            return CreateErrorResponse(message, "Viewers cannot receive domain messages");
        }

        _logger.LogInformation("Human actor {ActorId} received domain message {MessageId}; notification routing is not yet implemented",
            Id.GetId(), message.Id);

        return CreateAckResponse(message);
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