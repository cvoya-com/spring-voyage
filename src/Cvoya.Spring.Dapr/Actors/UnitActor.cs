// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Auth;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
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
    /// <summary>
    /// Maximum number of levels walked during cycle detection before the walk
    /// is treated as itself a cycle signal. Keeps <see cref="AddMemberAsync"/>
    /// bounded even in the face of pathological graphs.
    /// </summary>
    internal const int MaxCycleDetectionDepth = 64;

    private readonly ILogger _logger;
    private readonly IOrchestrationStrategy _orchestrationStrategy;
    private readonly IActivityEventBus _activityEventBus;
    private readonly IDirectoryService _directoryService;
    private readonly IActorProxyFactory _actorProxyFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnitActor"/> class.
    /// </summary>
    /// <param name="host">The actor host providing runtime services.</param>
    /// <param name="loggerFactory">The logger factory for creating loggers.</param>
    /// <param name="orchestrationStrategy">The strategy used to orchestrate domain messages.</param>
    /// <param name="activityEventBus">The activity event bus for emitting observable events.</param>
    /// <param name="directoryService">Directory used to resolve <c>unit://</c> member paths to actor ids during cycle detection.</param>
    /// <param name="actorProxyFactory">Factory used to build <see cref="IUnitActor"/> proxies for sub-units during cycle detection.</param>
    public UnitActor(
        ActorHost host,
        ILoggerFactory loggerFactory,
        IOrchestrationStrategy orchestrationStrategy,
        IActivityEventBus activityEventBus,
        IDirectoryService directoryService,
        IActorProxyFactory actorProxyFactory)
        : base(host)
    {
        _logger = loggerFactory.CreateLogger<UnitActor>();
        _orchestrationStrategy = orchestrationStrategy;
        _activityEventBus = activityEventBus;
        _directoryService = directoryService;
        _actorProxyFactory = actorProxyFactory;
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
        ArgumentNullException.ThrowIfNull(member);

        var members = await GetMembersListAsync(ct);

        if (members.Exists(m => m == member))
        {
            _logger.LogWarning("Unit {ActorId} already contains member {Member}", Id.GetId(), member);
            return;
        }

        // Cycle detection only applies to unit-typed members — agents can
        // belong to at most one unit (1:N parent) and are leaves, so they
        // cannot introduce a cycle in the containment graph.
        if (string.Equals(member.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
        {
            await EnsureNoCycleAsync(member, ct);
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
    public async Task<Address[]> GetMembersAsync(CancellationToken ct = default)
    {
        var members = await GetMembersListAsync(ct);
        return members.ToArray();
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
    public async Task<UnitPermissionEntry[]> GetHumanPermissionsAsync(CancellationToken ct = default)
    {
        var permissions = await GetHumanPermissionsMapAsync(ct);
        return permissions.Values.ToArray();
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
        var directoryFields = new List<string>();

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
        // directory entity is the source of truth (#123). We still emit a
        // StateChanged activity event for audit consistency so the API layer
        // does not need to duplicate the emission when only directory-side
        // fields change.
        if (metadata.DisplayName is not null)
        {
            directoryFields.Add(nameof(metadata.DisplayName));
        }

        if (metadata.Description is not null)
        {
            directoryFields.Add(nameof(metadata.Description));
        }

        if (writtenFields.Count == 0 && directoryFields.Count == 0)
        {
            _logger.LogDebug(
                "Unit {ActorId} SetMetadataAsync called with no fields; nothing to emit.",
                Id.GetId());
            return;
        }

        var allFields = writtenFields.Concat(directoryFields).ToList();

        _logger.LogInformation(
            "Unit {ActorId} metadata updated. Actor-owned fields: {ActorFields}; directory-owned fields: {DirectoryFields}",
            Id.GetId(),
            string.Join(",", writtenFields),
            string.Join(",", directoryFields));

        await EmitActivityEventAsync(ActivityEventType.StateChanged,
            $"Unit metadata updated: {string.Join(", ", allFields)}",
            ct,
            details: JsonSerializer.SerializeToElement(new
            {
                action = "MetadataUpdated",
                fields = allFields,
                actorFields = writtenFields,
                directoryFields,
                model = metadata.Model,
                color = metadata.Color,
                displayName = metadata.DisplayName,
                description = metadata.Description
            }));
    }

    /// <inheritdoc />
    public async Task<UnitConnectorBinding?> GetConnectorBindingAsync(CancellationToken ct = default)
    {
        var result = await StateManager.TryGetStateAsync<UnitConnectorBinding>(
            StateKeys.UnitConnectorBinding, ct);
        return result.HasValue ? result.Value : null;
    }

    /// <inheritdoc />
    public async Task SetConnectorBindingAsync(UnitConnectorBinding? binding, CancellationToken ct = default)
    {
        if (binding is null)
        {
            await StateManager.RemoveStateAsync(StateKeys.UnitConnectorBinding, ct);
            // Clearing the binding also drops any connector-owned runtime
            // metadata — it would otherwise leak across a rebind to a
            // different connector type.
            await StateManager.RemoveStateAsync(StateKeys.UnitConnectorMetadata, ct);
            _logger.LogInformation(
                "Unit {ActorId} cleared connector binding",
                Id.GetId());
            return;
        }

        await StateManager.SetStateAsync(StateKeys.UnitConnectorBinding, binding, ct);
        _logger.LogInformation(
            "Unit {ActorId} bound to connector type {TypeId}",
            Id.GetId(), binding.TypeId);
    }

    /// <inheritdoc />
    public async Task<JsonElement?> GetConnectorMetadataAsync(CancellationToken ct = default)
    {
        var result = await StateManager.TryGetStateAsync<JsonElement>(
            StateKeys.UnitConnectorMetadata, ct);
        return result.HasValue ? result.Value : null;
    }

    /// <inheritdoc />
    public async Task SetConnectorMetadataAsync(JsonElement? metadata, CancellationToken ct = default)
    {
        if (metadata is null || metadata.Value.ValueKind == JsonValueKind.Null ||
            metadata.Value.ValueKind == JsonValueKind.Undefined)
        {
            await StateManager.RemoveStateAsync(StateKeys.UnitConnectorMetadata, ct);
            return;
        }

        await StateManager.SetStateAsync(StateKeys.UnitConnectorMetadata, metadata.Value, ct);
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
    /// Handles a status query by returning the unit status, member count,
    /// and the full members list. The members array is a new field added in
    /// #339 alongside the new router-bypass read path in
    /// <c>UnitEndpoints.GetUnitAsync</c> so the two sources emit the same
    /// shape — the UI and e2e/12-nested-units scenario rely on inspecting
    /// the member list to verify containment.
    /// </summary>
    private async Task<Message?> HandleStatusQueryAsync(CancellationToken ct)
    {
        var members = await GetMembersListAsync(ct);
        var status = await GetStatusInternalAsync(ct);

        var statusPayload = JsonSerializer.SerializeToElement(new
        {
            Status = status.ToString(),
            MemberCount = members.Count,
            Members = members.Select(m => new { Scheme = m.Scheme, Path = m.Path }).ToArray(),
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
    /// Verifies that adding <paramref name="candidate"/> as a <c>unit://</c>
    /// member of this unit would not introduce a cycle. Throws
    /// <see cref="CyclicMembershipException"/> on self-loop, back-edge, or
    /// when the walk exceeds <see cref="MaxCycleDetectionDepth"/>.
    /// <para>
    /// The walk resolves each candidate path to its backing
    /// <see cref="IUnitActor"/> proxy via the directory and reads its current
    /// members. Missing or non-unit members are treated as dead ends — they
    /// cannot close a cycle.
    /// </para>
    /// </summary>
    private async Task EnsureNoCycleAsync(Address candidate, CancellationToken ct)
    {
        var selfAddress = Address;
        var selfActorId = Id.GetId();

        // Fast self-loop check: candidate resolves (by address equality) to
        // this same actor. Works even if the candidate was addressed via
        // path-form rather than actor-id form — the path-form case is caught
        // one level below after directory resolution.
        if (candidate == selfAddress)
        {
            throw BuildCycleException(selfAddress, candidate, [candidate],
                $"Unit '{selfAddress}' cannot be added as a member of itself.");
        }

        // Walk the candidate's sub-unit graph breadth-first. Whenever we
        // land on an actor whose id matches this unit's actor id, a cycle
        // exists and we must reject the add.
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<(Address Unit, IReadOnlyList<Address> PathFromCandidate)>();
        queue.Enqueue((candidate, [candidate]));

        while (queue.Count > 0)
        {
            var (current, pathFromCandidate) = queue.Dequeue();

            if (pathFromCandidate.Count > MaxCycleDetectionDepth)
            {
                _logger.LogWarning(
                    "Unit {ActorId} rejected adding member {Candidate}: cycle-detection walk exceeded max depth {MaxDepth}. Path: {Path}",
                    selfActorId, candidate, MaxCycleDetectionDepth, DescribePath(pathFromCandidate));

                throw BuildCycleException(selfAddress, candidate, pathFromCandidate,
                    $"Adding '{candidate}' to unit '{selfAddress}' would exceed the maximum unit-nesting depth ({MaxCycleDetectionDepth}). Treating as a cycle.");
            }

            DirectoryEntry? entry;
            try
            {
                entry = await _directoryService.ResolveAsync(current, ct);
            }
            catch (Exception ex) when (ex is not SpringException)
            {
                // Directory read failures during traversal should not poison
                // the add — they look like "unreachable" and surface as a
                // log-worthy warning, not a cycle.
                _logger.LogWarning(ex,
                    "Unit {ActorId} cycle-check: failed to resolve {Unit}; treating as dead end.",
                    selfActorId, current);
                continue;
            }

            if (entry is null)
            {
                // Unknown unit — not a cycle via this path.
                continue;
            }

            // Back-edge check: did we just land on this unit?
            if (string.Equals(entry.ActorId, selfActorId, StringComparison.Ordinal))
            {
                var cyclePath = pathFromCandidate.Append(selfAddress).ToList();

                _logger.LogWarning(
                    "Unit {ActorId} rejected adding member {Candidate}: cycle detected. Path: {Path}",
                    selfActorId, candidate, DescribePath(cyclePath));

                throw BuildCycleException(selfAddress, candidate, cyclePath,
                    $"Adding '{candidate}' to unit '{selfAddress}' would create a membership cycle: {DescribePath(cyclePath)}.");
            }

            // Mark this unit as visited by actor id so different address
            // spellings (e.g. path-form and uuid-form of the same unit) are
            // coalesced and we cannot get stuck on a benign sub-graph cycle
            // that does not involve this unit.
            if (!visited.Add(entry.ActorId))
            {
                continue;
            }

            Address[] subMembers;
            try
            {
                var proxy = _actorProxyFactory.CreateActorProxy<IUnitActor>(
                    new ActorId(entry.ActorId), nameof(UnitActor));
                subMembers = await proxy.GetMembersAsync(ct);
            }
            catch (Exception ex) when (ex is not SpringException)
            {
                // If the sub-unit is deleted or otherwise unreachable mid-walk,
                // treat as "not a cycle via that path" and continue.
                _logger.LogWarning(ex,
                    "Unit {ActorId} cycle-check: failed to read members of {Unit} (actorId={SubActorId}); treating as dead end.",
                    selfActorId, current, entry.ActorId);
                continue;
            }

            foreach (var sub in subMembers)
            {
                if (!string.Equals(sub.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var nextPath = pathFromCandidate.Append(sub).ToList();
                queue.Enqueue((sub, nextPath));
            }
        }
    }

    private static string DescribePath(IReadOnlyList<Address> path) =>
        string.Join(" -> ", path.Select(a => $"{a.Scheme}://{a.Path}"));

    private static CyclicMembershipException BuildCycleException(
        Address parent, Address candidate, IReadOnlyList<Address> path, string message) =>
        new(parent, candidate, path, message);

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