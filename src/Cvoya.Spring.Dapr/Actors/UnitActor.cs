// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Auth;

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
    private readonly IActivityEventBus _activityEventBus;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnitActor"/> class.
    /// </summary>
    /// <param name="host">The actor host providing runtime services.</param>
    /// <param name="loggerFactory">The logger factory for creating loggers.</param>
    /// <param name="orchestrationStrategy">The strategy used to orchestrate domain messages.</param>
    /// <param name="activityEventBus">The activity event bus for emitting observable events.</param>
    public UnitActor(ActorHost host, ILoggerFactory loggerFactory, IOrchestrationStrategy orchestrationStrategy, IActivityEventBus activityEventBus)
        : base(host)
    {
        _logger = loggerFactory.CreateLogger<UnitActor>();
        _orchestrationStrategy = orchestrationStrategy;
        _activityEventBus = activityEventBus;
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
            await EmitActivityEventAsync(ActivityEventType.MessageReceived,
                $"Received {message.Type} message {message.Id} from {message.From}",
                ct);

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

            await EmitActivityEventAsync(ActivityEventType.ErrorOccurred,
                $"Error processing message {message.Id}: {ex.Message}",
                ct);

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

        await EmitActivityEventAsync(ActivityEventType.StateChanged,
            $"Member {member} added to unit. Total members: {members.Count}",
            ct,
            details: JsonSerializer.SerializeToElement(new
            {
                action = "MemberAdded",
                member = $"{member.Scheme}://{member.Path}",
                totalMembers = members.Count
            }));
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

        await EmitActivityEventAsync(ActivityEventType.StateChanged,
            $"Member {member} removed from unit. Total members: {members.Count}",
            ct,
            details: JsonSerializer.SerializeToElement(new
            {
                action = "MemberRemoved",
                member = $"{member.Scheme}://{member.Path}",
                totalMembers = members.Count
            }));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Address>> GetMembersAsync(CancellationToken ct = default)
    {
        var members = await GetMembersListAsync(ct);
        return members.AsReadOnly();
    }

    /// <inheritdoc />
    public async Task SetHumanPermissionAsync(string humanId, UnitPermissionEntry entry, CancellationToken ct = default)
    {
        var permissions = await GetHumanPermissionsMapAsync(ct);
        permissions[humanId] = entry;
        await StateManager.SetStateAsync(StateKeys.HumanPermissions, permissions, ct);

        _logger.LogInformation(
            "Unit {ActorId} set permission for human {HumanId} to {Permission}",
            Id.GetId(), humanId, entry.Permission);
    }

    /// <inheritdoc />
    public async Task<PermissionLevel?> GetHumanPermissionAsync(string humanId, CancellationToken ct = default)
    {
        var permissions = await GetHumanPermissionsMapAsync(ct);
        return permissions.TryGetValue(humanId, out var entry) ? entry.Permission : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<UnitPermissionEntry>> GetHumanPermissionsAsync(CancellationToken ct = default)
    {
        var permissions = await GetHumanPermissionsMapAsync(ct);
        return permissions.Values.ToList().AsReadOnly();
    }

    /// <inheritdoc />
    public Task<UnitStatus> GetStatusAsync(CancellationToken ct = default)
        => GetStatusInternalAsync(ct);

    /// <inheritdoc />
    public async Task<TransitionResult> TransitionAsync(UnitStatus target, CancellationToken ct = default)
    {
        var current = await GetStatusInternalAsync(ct);

        if (!IsTransitionAllowed(current, target))
        {
            var reason = $"cannot transition from {current} to {target}";
            _logger.LogWarning(
                "Unit {ActorId} rejected transition from {Current} to {Target}: {Reason}",
                Id.GetId(), current, target, reason);
            return new TransitionResult(false, current, reason);
        }

        await StateManager.SetStateAsync(StateKeys.UnitStatus, target, ct);

        _logger.LogInformation(
            "Unit {ActorId} transitioned from {Current} to {Target}",
            Id.GetId(), current, target);

        await EmitActivityEventAsync(ActivityEventType.StateChanged,
            $"Unit transitioned from {current} to {target}",
            ct,
            details: JsonSerializer.SerializeToElement(new
            {
                action = "StatusTransition",
                from = current.ToString(),
                to = target.ToString()
            }));

        return new TransitionResult(true, target, null);
    }

    /// <inheritdoc />
    public async Task<UnitMetadata> GetMetadataAsync(CancellationToken ct = default)
    {
        var modelResult = await StateManager.TryGetStateAsync<string>(StateKeys.UnitModel, ct);
        var colorResult = await StateManager.TryGetStateAsync<string>(StateKeys.UnitColor, ct);

        // DisplayName and Description are persisted on the directory entity,
        // not on the actor. See IUnitActor.GetMetadataAsync for the contract.
        return new UnitMetadata(
            DisplayName: null,
            Description: null,
            Model: modelResult.HasValue ? modelResult.Value : null,
            Color: colorResult.HasValue ? colorResult.Value : null);
    }

    /// <inheritdoc />
    public async Task SetMetadataAsync(UnitMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var writtenFields = new List<string>();

        if (metadata.Model is not null)
        {
            await StateManager.SetStateAsync(StateKeys.UnitModel, metadata.Model, ct);
            writtenFields.Add(nameof(metadata.Model));
        }

        if (metadata.Color is not null)
        {
            await StateManager.SetStateAsync(StateKeys.UnitColor, metadata.Color, ct);
            writtenFields.Add(nameof(metadata.Color));
        }

        // DisplayName and Description are deliberately not persisted here; the
        // directory entity is the source of truth. We still allow callers to
        // pass them so higher layers can share a single metadata record.
        if (writtenFields.Count == 0)
        {
            _logger.LogDebug(
                "Unit {ActorId} SetMetadataAsync called with no actor-owned fields; nothing persisted.",
                Id.GetId());
            return;
        }

        _logger.LogInformation(
            "Unit {ActorId} metadata updated. Fields: {Fields}",
            Id.GetId(), string.Join(",", writtenFields));

        await EmitActivityEventAsync(ActivityEventType.StateChanged,
            $"Unit metadata updated: {string.Join(", ", writtenFields)}",
            ct,
            details: JsonSerializer.SerializeToElement(new
            {
                action = "MetadataUpdated",
                fields = writtenFields,
                model = metadata.Model,
                color = metadata.Color
            }));
    }

    /// <summary>
    /// Reads the persisted lifecycle status, defaulting to <see cref="UnitStatus.Draft"/> when unset.
    /// </summary>
    private async Task<UnitStatus> GetStatusInternalAsync(CancellationToken ct)
    {
        var result = await StateManager
            .TryGetStateAsync<UnitStatus>(StateKeys.UnitStatus, ct);

        return result.HasValue ? result.Value : UnitStatus.Draft;
    }

    /// <summary>
    /// Enforces the unit lifecycle state machine.
    /// </summary>
    private static bool IsTransitionAllowed(UnitStatus current, UnitStatus target) =>
        (current, target) switch
        {
            (UnitStatus.Draft, UnitStatus.Stopped) => true,
            (UnitStatus.Stopped, UnitStatus.Starting) => true,
            (UnitStatus.Starting, UnitStatus.Running) => true,
            (UnitStatus.Starting, UnitStatus.Error) => true,
            (UnitStatus.Running, UnitStatus.Stopping) => true,
            (UnitStatus.Stopping, UnitStatus.Stopped) => true,
            (UnitStatus.Stopping, UnitStatus.Error) => true,
            (UnitStatus.Error, UnitStatus.Stopped) => true,
            _ => false,
        };

    /// <summary>
    /// Retrieves the human permissions map from state, returning an empty dictionary if none exists.
    /// </summary>
    private async Task<Dictionary<string, UnitPermissionEntry>> GetHumanPermissionsMapAsync(CancellationToken ct)
    {
        var result = await StateManager
            .TryGetStateAsync<Dictionary<string, UnitPermissionEntry>>(StateKeys.HumanPermissions, ct);

        return result.HasValue ? result.Value : [];
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
        var status = await GetStatusInternalAsync(ct);

        var statusPayload = JsonSerializer.SerializeToElement(new
        {
            Status = status.ToString(),
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

        await EmitActivityEventAsync(ActivityEventType.DecisionMade,
            $"Delegating message {message.Id} to orchestration strategy with {members.Count} members",
            ct,
            details: JsonSerializer.SerializeToElement(new
            {
                decision = "DelegateToStrategy",
                messageId = message.Id,
                memberCount = members.Count
            }),
            correlationId: message.ConversationId);

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
    /// Emits an activity event through the activity event bus.
    /// Failures are logged but never allowed to escape the actor turn.
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
                ActivityEventType.StateChanged => ActivitySeverity.Debug,
                _ => ActivitySeverity.Info,
            };

            var activityEvent = new ActivityEvent(
                Guid.NewGuid(),
                DateTimeOffset.UtcNow,
                Address,
                eventType,
                severity,
                description,
                details,
                correlationId);

            await _activityEventBus.PublishAsync(activityEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit activity event {EventType} for unit actor {ActorId}.",
                eventType, Id.GetId());
        }
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