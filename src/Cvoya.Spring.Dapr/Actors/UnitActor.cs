// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;

using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

/// <summary>
/// Dapr virtual actor representing a unit in the Spring Voyage platform.
/// A unit groups agents and sub-units, dispatching domain messages through
/// a configurable <see cref="IOrchestrationStrategy"/> while handling
/// control messages (cancel, status, health, policy) directly.
/// </summary>
public class UnitActor : Actor, IUnitActor
{
    private readonly ILogger _logger;
    private readonly IOrchestrationStrategy _orchestrationStrategy;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnitActor"/> class.
    /// </summary>
    /// <param name="host">The actor host providing runtime services.</param>
    /// <param name="loggerFactory">The logger factory for creating loggers.</param>
    /// <param name="orchestrationStrategy">The strategy used to orchestrate domain messages.</param>
    public UnitActor(ActorHost host, ILoggerFactory loggerFactory, IOrchestrationStrategy orchestrationStrategy)
        : base(host)
    {
        _logger = loggerFactory.CreateLogger<UnitActor>();
        _orchestrationStrategy = orchestrationStrategy;
    }

    /// <summary>
    /// Gets the address of this unit actor.
    /// </summary>
    public Address Address => new("unit", Id.GetId());

    /// <inheritdoc />
    public async Task<Message?> ReceiveAsync(Message message, CancellationToken ct = default)
    {
        try
        {
            return message.Type switch
            {
                MessageType.Cancel => await HandleCancelAsync(message, ct),
                MessageType.StatusQuery => await HandleStatusQueryAsync(ct),
                MessageType.HealthCheck => HandleHealthCheck(message),
                MessageType.PolicyUpdate => await HandlePolicyUpdateAsync(message, ct),
                MessageType.Domain => await HandleDomainMessageAsync(message, ct),
                _ => throw new SpringException($"Unknown message type: {message.Type}")
            };
        }
        catch (Exception ex) when (ex is not SpringException)
        {
            _logger.LogError(ex,
                "Unhandled exception processing message {MessageId} of type {MessageType} in unit actor {ActorId}",
                message.Id, message.Type, Id.GetId());
            return CreateErrorResponse(message, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task AddMemberAsync(Address member, CancellationToken ct = default)
    {
        var members = await GetMembersListAsync(ct);

        if (members.Exists(m => m == member))
        {
            _logger.LogWarning("Unit {ActorId} already contains member {Member}", Id.GetId(), member);
            return;
        }

        members.Add(member);
        await StateManager.SetStateAsync(StateKeys.Members, members, ct);

        _logger.LogInformation("Unit {ActorId} added member {Member}. Total members: {Count}",
            Id.GetId(), member, members.Count);
    }

    /// <inheritdoc />
    public async Task RemoveMemberAsync(Address member, CancellationToken ct = default)
    {
        var members = await GetMembersListAsync(ct);
        var removed = members.RemoveAll(m => m == member);

        if (removed == 0)
        {
            _logger.LogWarning("Unit {ActorId} does not contain member {Member}", Id.GetId(), member);
            return;
        }

        await StateManager.SetStateAsync(StateKeys.Members, members, ct);

        _logger.LogInformation("Unit {ActorId} removed member {Member}. Total members: {Count}",
            Id.GetId(), member, members.Count);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Address>> GetMembersAsync(CancellationToken ct = default)
    {
        var members = await GetMembersListAsync(ct);
        return members.AsReadOnly();
    }

    /// <summary>
    /// Handles a cancel message by logging the cancellation request.
    /// </summary>
    private Task<Message?> HandleCancelAsync(Message message, CancellationToken ct)
    {
        _ = ct;
        _logger.LogInformation("Unit {ActorId} received cancel for conversation {ConversationId}",
            Id.GetId(), message.ConversationId);

        return Task.FromResult<Message?>(CreateAckResponse(message));
    }

    /// <summary>
    /// Handles a status query by returning the unit status including member count.
    /// </summary>
    private async Task<Message?> HandleStatusQueryAsync(CancellationToken ct)
    {
        var members = await GetMembersListAsync(ct);

        var statusPayload = JsonSerializer.SerializeToElement(new
        {
            Status = "Active",
            MemberCount = members.Count
        });

        return new Message(
            Guid.NewGuid(),
            Address,
            Address, // Status queries are informational; no specific recipient.
            MessageType.StatusQuery,
            null,
            statusPayload,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Handles a health check by returning a healthy response.
    /// </summary>
    private Message? HandleHealthCheck(Message message)
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
    /// Handles a policy update by storing the updated policy payload.
    /// </summary>
    private async Task<Message?> HandlePolicyUpdateAsync(Message message, CancellationToken ct)
    {
        _logger.LogInformation("Unit {ActorId} received policy update", Id.GetId());
        await StateManager.SetStateAsync(StateKeys.Policies, message.Payload, ct);
        return CreateAckResponse(message);
    }

    /// <summary>
    /// Handles a domain message by delegating to the configured orchestration strategy.
    /// </summary>
    private async Task<Message?> HandleDomainMessageAsync(Message message, CancellationToken ct)
    {
        var members = await GetMembersListAsync(ct);
        var context = new UnitContext(Address, members.AsReadOnly(), _logger);

        _logger.LogInformation(
            "Unit {ActorId} delegating domain message {MessageId} to orchestration strategy with {MemberCount} members",
            Id.GetId(), message.Id, members.Count);

        return await _orchestrationStrategy.OrchestrateAsync(message, context, ct);
    }

    /// <summary>
    /// Retrieves the current member list from state, returning an empty list if none exists.
    /// </summary>
    private async Task<List<Address>> GetMembersListAsync(CancellationToken ct)
    {
        var result = await StateManager
            .TryGetStateAsync<List<Address>>(StateKeys.Members, ct)
            ;

        return result.HasValue ? result.Value : [];
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