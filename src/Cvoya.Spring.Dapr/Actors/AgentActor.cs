// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Messaging;

using global::Dapr.Actors;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

/// <summary>
/// Dapr virtual actor representing an agent in the Spring Voyage platform.
/// Implements a partitioned mailbox with three logical channel types:
/// control (highest priority), conversation (one per ConversationId), and observation (batched events).
/// The actor never performs long-running work in the actor turn; it dispatches async work externally.
/// </summary>
public class AgentActor(ActorHost host, ILoggerFactory loggerFactory) : Actor(host), IAgentActor
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<AgentActor>();
    private CancellationTokenSource? _activeWorkCancellation;

    /// <summary>
    /// Gets the address of this agent actor.
    /// </summary>
    public Address Address => new("agent", Id.GetId());

    /// <inheritdoc />
    public async Task<Message?> ReceiveAsync(Message message, CancellationToken cancellationToken = default)
    {
        try
        {
            return message.Type switch
            {
                MessageType.Cancel => await HandleCancelAsync(message, cancellationToken),
                MessageType.StatusQuery => await HandleStatusQueryAsync(message, cancellationToken),
                MessageType.HealthCheck => await HandleHealthCheckAsync(message, cancellationToken),
                MessageType.PolicyUpdate => await HandlePolicyUpdateAsync(message, cancellationToken),
                MessageType.Domain => await HandleDomainMessageAsync(message, cancellationToken),
                _ => throw new SpringException($"Unknown message type: {message.Type}")
            };
        }
        catch (Exception ex) when (ex is not SpringException)
        {
            _logger.LogError(ex, "Unhandled exception processing message {MessageId} of type {MessageType} in actor {ActorId}",
                message.Id, message.Type, Id.GetId());
            return CreateErrorResponse(message, ex.Message);
        }
    }

    /// <summary>
    /// Handles a cancel message by cancelling the active work token source.
    /// </summary>
    private async Task<Message?> HandleCancelAsync(Message message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Actor {ActorId} received cancel for conversation {ConversationId}",
            Id.GetId(), message.ConversationId);

        if (_activeWorkCancellation is not null)
        {
            await _activeWorkCancellation.CancelAsync();
            _activeWorkCancellation.Dispose();
            _activeWorkCancellation = null;
        }

        var activeConversation = await StateManager
            .TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, cancellationToken)
            ;

        if (activeConversation.HasValue &&
            activeConversation.Value.ConversationId == message.ConversationId)
        {
            await StateManager.TryRemoveStateAsync(StateKeys.ActiveConversation, cancellationToken);
            await PromoteNextPendingAsync(cancellationToken);
        }

        return CreateAckResponse(message);
    }

    /// <summary>
    /// Handles a status query message by returning the current agent status.
    /// </summary>
    private async Task<Message?> HandleStatusQueryAsync(Message message, CancellationToken cancellationToken)
    {
        var status = await GetCurrentStatusAsync(cancellationToken);
        var activeConversation = await StateManager
            .TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, cancellationToken)
            ;
        var pending = await StateManager
            .TryGetStateAsync<List<ConversationChannel>>(StateKeys.PendingConversations, cancellationToken)
            ;

        var statusPayload = JsonSerializer.SerializeToElement(new
        {
            Status = status.ToString(),
            ActiveConversationId = activeConversation.HasValue ? activeConversation.Value.ConversationId : null,
            PendingConversationCount = pending.HasValue ? pending.Value.Count : 0
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
    private Task<Message?> HandleHealthCheckAsync(Message message, CancellationToken cancellationToken)
    {
        _ = cancellationToken; // Unused but kept for signature consistency.
        var healthPayload = JsonSerializer.SerializeToElement(new { Healthy = true });

        Message? response = new Message(
            Guid.NewGuid(),
            Address,
            message.From,
            MessageType.HealthCheck,
            message.ConversationId,
            healthPayload,
            DateTimeOffset.UtcNow);

        return Task.FromResult<Message?>(response);
    }

    /// <summary>
    /// Handles a policy update message by storing the updated policy.
    /// </summary>
    private async Task<Message?> HandlePolicyUpdateAsync(Message message, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Actor {ActorId} received policy update", Id.GetId());

        // Store the policy update payload for future reference.
        await StateManager.SetStateAsync("Agent:LastPolicyUpdate", message.Payload, cancellationToken);

        return CreateAckResponse(message);
    }

    /// <summary>
    /// Handles a domain message by routing it to the appropriate conversation channel.
    /// New conversations are created if the ConversationId is unseen.
    /// If there is already an active conversation for a different ConversationId, the new conversation is queued as pending.
    /// </summary>
    private async Task<Message?> HandleDomainMessageAsync(Message message, CancellationToken cancellationToken)
    {
        var conversationId = message.ConversationId
            ?? throw new SpringException("Domain messages must have a ConversationId");

        var activeConversation = await StateManager
            .TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, cancellationToken)
            ;

        // Case 1: No active conversation — make this the active one.
        if (!activeConversation.HasValue)
        {
            var channel = new ConversationChannel
            {
                ConversationId = conversationId,
                Messages = [message],
                CreatedAt = DateTimeOffset.UtcNow
            };

            await StateManager.SetStateAsync(StateKeys.ActiveConversation, channel, cancellationToken);
            _activeWorkCancellation = new CancellationTokenSource();

            _logger.LogInformation("Actor {ActorId} activated conversation {ConversationId}",
                Id.GetId(), conversationId);

            return CreateAckResponse(message);
        }

        // Case 2: Message belongs to the active conversation — append to it.
        if (activeConversation.Value.ConversationId == conversationId)
        {
            var channel = activeConversation.Value;
            channel.Messages.Add(message);
            await StateManager.SetStateAsync(StateKeys.ActiveConversation, channel, cancellationToken);

            _logger.LogInformation("Actor {ActorId} appended message to active conversation {ConversationId}",
                Id.GetId(), conversationId);

            return CreateAckResponse(message);
        }

        // Case 3: Different conversation — route to pending.
        await EnqueuePendingMessageAsync(conversationId, message, cancellationToken);

        _logger.LogInformation("Actor {ActorId} queued message for pending conversation {ConversationId}",
            Id.GetId(), conversationId);

        return CreateAckResponse(message);
    }

    /// <summary>
    /// Suspends the currently active conversation and moves it to the pending list.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    internal async Task SuspendActiveConversationAsync(CancellationToken cancellationToken = default)
    {
        var activeConversation = await StateManager
            .TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, cancellationToken)
            ;

        if (!activeConversation.HasValue)
        {
            return;
        }

        if (_activeWorkCancellation is not null)
        {
            await _activeWorkCancellation.CancelAsync();
            _activeWorkCancellation.Dispose();
            _activeWorkCancellation = null;
        }

        var pending = await StateManager
            .TryGetStateAsync<List<ConversationChannel>>(StateKeys.PendingConversations, cancellationToken)
            ;

        var pendingList = pending.HasValue ? pending.Value : [];
        pendingList.Add(activeConversation.Value);

        await StateManager.SetStateAsync(StateKeys.PendingConversations, pendingList, cancellationToken);
        await StateManager.TryRemoveStateAsync(StateKeys.ActiveConversation, cancellationToken);

        _logger.LogInformation("Actor {ActorId} suspended conversation {ConversationId}",
            Id.GetId(), activeConversation.Value.ConversationId);
    }

    /// <summary>
    /// Promotes the next pending conversation to active status.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    internal async Task PromoteNextPendingAsync(CancellationToken cancellationToken = default)
    {
        var pending = await StateManager
            .TryGetStateAsync<List<ConversationChannel>>(StateKeys.PendingConversations, cancellationToken)
            ;

        if (!pending.HasValue || pending.Value.Count == 0)
        {
            return;
        }

        var pendingList = pending.Value;
        var next = pendingList[0];
        pendingList.RemoveAt(0);

        await StateManager.SetStateAsync(StateKeys.ActiveConversation, next, cancellationToken);

        if (pendingList.Count > 0)
        {
            await StateManager.SetStateAsync(StateKeys.PendingConversations, pendingList, cancellationToken);
        }
        else
        {
            await StateManager.TryRemoveStateAsync(StateKeys.PendingConversations, cancellationToken);
        }

        _activeWorkCancellation = new CancellationTokenSource();

        _logger.LogInformation("Actor {ActorId} promoted conversation {ConversationId} to active",
            Id.GetId(), next.ConversationId);
    }

    /// <summary>
    /// Enqueues a message for a pending conversation, creating the channel if it does not exist.
    /// </summary>
    private async Task EnqueuePendingMessageAsync(string conversationId, Message message, CancellationToken cancellationToken)
    {
        var pending = await StateManager
            .TryGetStateAsync<List<ConversationChannel>>(StateKeys.PendingConversations, cancellationToken)
            ;

        var pendingList = pending.HasValue ? pending.Value : [];

        var existingChannel = pendingList.Find(c => c.ConversationId == conversationId);
        if (existingChannel is not null)
        {
            existingChannel.Messages.Add(message);
        }
        else
        {
            pendingList.Add(new ConversationChannel
            {
                ConversationId = conversationId,
                Messages = [message],
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        await StateManager.SetStateAsync(StateKeys.PendingConversations, pendingList, cancellationToken);
    }

    /// <summary>
    /// Gets the current status of the agent based on its state.
    /// </summary>
    private async Task<AgentStatus> GetCurrentStatusAsync(CancellationToken cancellationToken)
    {
        var activeConversation = await StateManager
            .TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, cancellationToken)
            ;

        if (!activeConversation.HasValue)
        {
            return AgentStatus.Idle;
        }

        return AgentStatus.Active;
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