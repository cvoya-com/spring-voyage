// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using System.Linq;
using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;

using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

/// <summary>
/// Dapr virtual actor representing a human user in the Spring Voyage platform.
/// Humans have identity, permission levels, and notification preferences.
/// Domain messages are rejected for viewers; all other permission levels receive
/// an acknowledgment (notification routing is future work).
/// </summary>
public class HumanActor(
    ActorHost host,
    IActivityEventBus activityEventBus,
    ILoggerFactory loggerFactory) : Actor(host), IHumanActor
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
            // #456: humans are first-class observers — every domain message
            // addressed to them is published to the activity bus as a
            // MessageReceived event, correlated to the thread id. This
            // is what the Inbox query service consumes to answer "what's
            // waiting on me?". Keep the emission on the hot path (before the
            // rejection branch below) so a denied message still leaves an
            // audit trail.
            // #1209: persist the message envelope (sender / recipient /
            // payload) on the event so `spring inbox show` can render the
            // body inline, not just the summary line.
            if (message.Type == MessageType.Domain)
            {
                await EmitActivityEventAsync(
                    ActivityEventType.MessageReceived,
                    $"Received Domain message {message.Id} from {message.From}",
                    cancellationToken,
                    details: MessageReceivedDetails.Build(message),
                    correlationId: message.ThreadId);
            }

            return message.Type switch
            {
                MessageType.StatusQuery => await HandleStatusQueryAsync(message, cancellationToken),
                MessageType.HealthCheck => HandleHealthCheck(message),
                MessageType.Domain => await HandleDomainMessageAsync(message, cancellationToken),
                _ => throw new CallerValidationException(
                    CallerValidationCodes.UnknownMessageType,
                    $"Unknown message type: {message.Type}")
            };
        }
        catch (Exception ex) when (ex is not SpringException)
        {
            _logger.LogError(ex, "Unhandled exception processing message {MessageId} of type {MessageType} in human actor {ActorId}",
                message.Id, message.Type, Id.GetId());
            return CreateErrorResponse(message, ex.Message);
        }
    }

    /// <summary>
    /// Publishes a single activity event on behalf of this human actor. Errors
    /// are swallowed (same pattern as <see cref="AgentActor"/> /
    /// <see cref="UnitActor"/>) so a failing observability pipeline never
    /// tears down message delivery.
    /// </summary>
    private async Task EmitActivityEventAsync(
        ActivityEventType eventType,
        string description,
        CancellationToken cancellationToken,
        JsonElement? details = null,
        string? correlationId = null)
    {
        try
        {
            var severity = eventType switch
            {
                ActivityEventType.ErrorOccurred => ActivitySeverity.Error,
                _ => ActivitySeverity.Info,
            };

            var evt = new ActivityEvent(
                Id: Guid.NewGuid(),
                Timestamp: DateTimeOffset.UtcNow,
                Source: Address,
                EventType: eventType,
                Severity: severity,
                Summary: description,
                Details: details,
                CorrelationId: correlationId,
                Cost: null);

            await activityEventBus.PublishAsync(evt, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to emit {EventType} activity event from human actor {ActorId}",
                eventType, Id.GetId());
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Defaults to <see cref="PermissionLevel.Operator"/> when no explicit
    /// state has been written for this human actor. The previous default
    /// (<see cref="PermissionLevel.Viewer"/>) caused inbound Domain messages
    /// — including the agent's reply to a thread the user themselves
    /// started — to be rejected with "insufficient permission (Viewer)",
    /// breaking the new-conversation round-trip in the OSS deployment
    /// (#1473, #1476).
    ///
    /// Defaulting to Operator is an interim OSS unblocker, NOT the
    /// long-term shape of the permission model. Tracked under #1479,
    /// which redesigns the model around owner-by-creation and
    /// thread-scoped participation. Once that lands this default goes
    /// away and the per-resource ownership / per-thread membership
    /// records become the source of truth.
    /// </remarks>
    public async Task<PermissionLevel> GetPermissionAsync(CancellationToken cancellationToken = default)
    {
        var result = await StateManager
            .TryGetStateAsync<PermissionLevel>(StateKeys.HumanPermission, cancellationToken)
            ;

        return result.HasValue ? result.Value : PermissionLevel.Operator;
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

    /// <inheritdoc />
    public async Task<PermissionLevel?> GetPermissionForUnitAsync(string unitId, CancellationToken cancellationToken = default)
    {
        var unitPermissions = await GetUnitPermissionsMapAsync(cancellationToken);
        return unitPermissions.TryGetValue(unitId, out var level) ? level : null;
    }

    /// <inheritdoc />
    public async Task SetPermissionForUnitAsync(string unitId, PermissionLevel level, CancellationToken cancellationToken = default)
    {
        var unitPermissions = await GetUnitPermissionsMapAsync(cancellationToken);
        unitPermissions[unitId] = level;
        await StateManager.SetStateAsync(StateKeys.HumanUnitPermissions, unitPermissions, cancellationToken);

        _logger.LogInformation(
            "Human actor {ActorId} permission for unit {UnitId} changed to {Permission}",
            Id.GetId(), unitId, level);
    }

    /// <inheritdoc />
    public async Task RemovePermissionForUnitAsync(string unitId, CancellationToken cancellationToken = default)
    {
        var unitPermissions = await GetUnitPermissionsMapAsync(cancellationToken);
        if (!unitPermissions.Remove(unitId))
        {
            // Idempotent: nothing to remove is not an error.
            return;
        }

        await StateManager.SetStateAsync(StateKeys.HumanUnitPermissions, unitPermissions, cancellationToken);

        _logger.LogInformation(
            "Human actor {ActorId} permission for unit {UnitId} cleared",
            Id.GetId(), unitId);
    }

    /// <inheritdoc />
    public async Task MarkReadAsync(string threadId, DateTimeOffset readAt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        var map = await GetLastReadAtMapAsync(cancellationToken);

        // Only advance the cursor — never move it backwards.
        if (map.TryGetValue(threadId, out var existing) && existing >= readAt)
        {
            return;
        }

        map[threadId] = readAt;
        await StateManager.SetStateAsync(StateKeys.HumanLastReadAt, map, cancellationToken);

        _logger.LogDebug(
            "Human actor {ActorId} marked thread {ThreadId} as read at {ReadAt}",
            Id.GetId(), threadId, readAt);
    }

    /// <inheritdoc />
    public async Task<ThreadReadEntry[]> GetLastReadAtAsync(CancellationToken cancellationToken = default)
    {
        var map = await GetLastReadAtMapAsync(cancellationToken);
        return [.. map.Select(kv => new ThreadReadEntry(kv.Key, kv.Value))];
    }

    /// <summary>
    /// Retrieves the per-thread last-read-at map from state. Returns a mutable
    /// dictionary; callers that write to it must persist the result explicitly.
    /// </summary>
    private async Task<Dictionary<string, DateTimeOffset>> GetLastReadAtMapAsync(CancellationToken cancellationToken)
    {
        var result = await StateManager
            .TryGetStateAsync<Dictionary<string, DateTimeOffset>>(StateKeys.HumanLastReadAt, cancellationToken);

        return result.HasValue ? result.Value : [];
    }

    /// <summary>
    /// Retrieves the unit-scoped permissions map from state.
    /// </summary>
    private async Task<Dictionary<string, PermissionLevel>> GetUnitPermissionsMapAsync(CancellationToken cancellationToken)
    {
        var result = await StateManager
            .TryGetStateAsync<Dictionary<string, PermissionLevel>>(StateKeys.HumanUnitPermissions, cancellationToken);

        return result.HasValue ? result.Value : [];
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
            message.ThreadId,
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
            message.ThreadId,
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
            originalMessage.ThreadId,
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
            originalMessage.ThreadId,
            errorPayload,
            DateTimeOffset.UtcNow);
    }
}