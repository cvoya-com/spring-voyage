// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Routing;

using global::Dapr.Actors;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

/// <summary>
/// Dapr virtual actor representing an agent in the Spring Voyage platform.
/// Implements a partitioned mailbox with three logical channel types:
/// control (highest priority), conversation (one per ConversationId), and observation (batched events).
/// The actor never performs long-running work in the actor turn; it dispatches async work externally.
/// </summary>
public class AgentActor(
    ActorHost host,
    IActivityEventBus activityEventBus,
    IInitiativeEngine initiativeEngine,
    IAgentPolicyStore policyStore,
    IExecutionDispatcher executionDispatcher,
    MessageRouter messageRouter,
    IAgentDefinitionProvider agentDefinitionProvider,
    IEnumerable<ISkillRegistry> skillRegistries,
    IUnitMembershipRepository membershipRepository,
    ILoggerFactory loggerFactory) : Actor(host), IAgentActor, IRemindable
{
    /// <summary>
    /// Name of the Dapr reminder that drives periodic initiative checks.
    /// </summary>
    internal const string InitiativeReminderName = "initiative-check";

    /// <summary>
    /// Maximum number of observations retained in the observation channel.
    /// Older entries are trimmed when the list exceeds this bound.
    /// </summary>
    internal const int MaxObservationChannelEntries = 100;

    private readonly ILogger _logger = loggerFactory.CreateLogger<AgentActor>();
    private readonly IReadOnlyList<ISkillRegistry> _skillRegistries = skillRegistries.ToList();
    private CancellationTokenSource? _activeWorkCancellation;

    /// <summary>
    /// Exposed for tests: the currently running dispatch task (if any).
    /// Production callers should not depend on this field.
    /// </summary>
    internal Task? PendingDispatchTask { get; private set; }

    /// <summary>
    /// Gets the address of this agent actor.
    /// </summary>
    public Address Address => new("agent", Id.GetId());

    /// <inheritdoc />
    public async Task<Message?> ReceiveAsync(Message message, CancellationToken cancellationToken = default)
    {
        try
        {
            await EmitActivityEventAsync(ActivityEventType.MessageReceived,
                $"Received {message.Type} message {message.Id} from {message.From}",
                cancellationToken);

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

            await EmitActivityEventAsync(ActivityEventType.ErrorOccurred,
                $"Error processing message {message.Id}: {ex.Message}",
                cancellationToken);

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

            await EmitActivityEventAsync(ActivityEventType.ConversationCompleted,
                $"Conversation {message.ConversationId} cancelled",
                cancellationToken,
                correlationId: message.ConversationId);

            await PromoteNextPendingAsync(cancellationToken);

            // If no pending conversation was promoted, agent returns to Idle.
            var newActive = await StateManager
                .TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, cancellationToken);
            if (!newActive.HasValue)
            {
                await EmitActivityEventAsync(ActivityEventType.StateChanged,
                    "State changed from Active to Idle",
                    cancellationToken,
                    details: JsonSerializer.SerializeToElement(new { from = "Active", to = "Idle" }));
            }
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
    /// When a new conversation is activated, the actor kicks off a fire-and-forget dispatch task so the actor turn returns quickly.
    /// </summary>
    private async Task<Message?> HandleDomainMessageAsync(Message message, CancellationToken cancellationToken)
    {
        var conversationId = message.ConversationId
            ?? throw new SpringException("Domain messages must have a ConversationId");

        // Resolve the per-turn effective metadata up front: the merge of the
        // agent's own global config with any per-membership override recorded
        // on the (sender-unit, agent) edge. If the membership is disabled, the
        // agent short-circuits before doing any dispatch work.
        var effective = await ResolveEffectiveMetadataAsync(message, cancellationToken);

        if (effective.Enabled == false)
        {
            _logger.LogInformation(
                "Actor {ActorId} skipping message {MessageId} from {Sender}: membership Enabled=false.",
                Id.GetId(), message.Id, message.From);

            await EmitActivityEventAsync(ActivityEventType.DecisionMade,
                $"Skipped message {message.Id} from {message.From}: membership disabled.",
                cancellationToken,
                details: JsonSerializer.SerializeToElement(new
                {
                    decision = "MembershipDisabled",
                    sender = new { scheme = message.From.Scheme, path = message.From.Path },
                    messageId = message.Id,
                }),
                correlationId: conversationId);

            return CreateAckResponse(message);
        }

        var activeConversation = await StateManager
            .TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, cancellationToken)
            ;

        // Case 1: No active conversation — make this the active one and dispatch.
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

            await EmitActivityEventAsync(ActivityEventType.ConversationStarted,
                $"Started conversation {conversationId}",
                cancellationToken,
                correlationId: conversationId);

            await EmitActivityEventAsync(ActivityEventType.StateChanged,
                "State changed from Idle to Active",
                cancellationToken,
                details: JsonSerializer.SerializeToElement(new { from = "Idle", to = "Active" }));

            var context = await BuildPromptAssemblyContextAsync(channel, effective, cancellationToken);
            PendingDispatchTask = RunDispatchAsync(message, context, _activeWorkCancellation.Token);

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

        await EmitActivityEventAsync(ActivityEventType.DecisionMade,
            $"Queued conversation {conversationId} as pending (active: {activeConversation.Value.ConversationId})",
            cancellationToken,
            details: JsonSerializer.SerializeToElement(new
            {
                decision = "QueueAsPending",
                activeConversationId = activeConversation.Value.ConversationId,
                pendingConversationId = conversationId
            }),
            correlationId: conversationId);

        return CreateAckResponse(message);
    }

    /// <summary>
    /// Builds the prompt-assembly context for the active conversation. Members
    /// and unit policies are intentionally left empty here — an agent actor does
    /// not know its enclosing unit, so unit context must be supplied by a
    /// UnitActor-side caller in future work. Skills come from registered
    /// <see cref="ISkillRegistry"/> instances; agent instructions come from
    /// <see cref="IAgentDefinitionProvider"/>. The <paramref name="effective"/>
    /// metadata is propagated so downstream consumers (model selection,
    /// execution-mode-aware dispatchers, specialty-aware orchestration) use
    /// the per-turn merged config rather than re-reading global state.
    /// </summary>
    private async Task<PromptAssemblyContext> BuildPromptAssemblyContextAsync(
        ConversationChannel channel,
        AgentMetadata effective,
        CancellationToken cancellationToken)
    {
        var definition = await agentDefinitionProvider.GetByIdAsync(Id.GetId(), cancellationToken);

        var skills = _skillRegistries
            .Select(r => new Skill(
                Name: r.Name,
                Description: $"Tools exposed by the {r.Name} connector.",
                Tools: r.GetToolDefinitions()))
            .ToList();

        return new PromptAssemblyContext(
            Members: [],
            Policies: null,
            Skills: skills,
            PriorMessages: channel.Messages.ToList(),
            LastCheckpoint: null,
            AgentInstructions: definition?.Instructions,
            EffectiveMetadata: effective);
    }

    /// <summary>
    /// Resolves the effective per-turn metadata for <paramref name="message"/>.
    /// Starts from the agent's global <see cref="AgentMetadata"/> (as stored
    /// on the actor) and, when the sender is a unit, overlays any
    /// per-membership override recorded on the
    /// <c>(message.From.Path, this-agent)</c> edge.
    /// <para>
    /// Merge rule (see #243): a non-<c>null</c> / non-default per-membership
    /// value wins over the agent-global value for the same field. When no
    /// membership row exists, or when the sender is not a unit (e.g., a
    /// webhook-originated message or a peer agent), the agent-global
    /// metadata is returned unchanged.
    /// </para>
    /// <para>
    /// This is a receive-path helper: non-message-turn surfaces such as
    /// <c>GET /agents/{id}</c> must continue to use <see cref="GetMetadataAsync"/>
    /// directly so callers observe global config, not the result of a merge
    /// against some arbitrary unit.
    /// </para>
    /// </summary>
    internal async Task<AgentMetadata> ResolveEffectiveMetadataAsync(
        Message message, CancellationToken cancellationToken)
    {
        var global = await GetMetadataAsync(cancellationToken);

        if (!string.Equals(message.From.Scheme, "unit", StringComparison.Ordinal))
        {
            return global;
        }

        UnitMembership? membership;
        try
        {
            membership = await membershipRepository.GetAsync(
                unitId: message.From.Path,
                agentAddress: Id.GetId(),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Membership lookup is advisory — a failure here must not block
            // message handling. Fall back to the agent's global config and
            // log so operators can spot persistent failures.
            _logger.LogWarning(ex,
                "Membership lookup failed for agent {ActorId} and unit {UnitId}; using agent-global metadata.",
                Id.GetId(), message.From.Path);
            return global;
        }

        if (membership is null)
        {
            return global;
        }

        // Per-membership fields win where they are set. Enabled is a
        // non-nullable bool on the membership row (defaults to true on
        // insert), so a false value here always takes effect — even if the
        // agent itself has Enabled=true or unset.
        return new AgentMetadata(
            Model: membership.Model ?? global.Model,
            Specialty: membership.Specialty ?? global.Specialty,
            Enabled: membership.Enabled,
            ExecutionMode: membership.ExecutionMode ?? global.ExecutionMode,
            ParentUnit: global.ParentUnit);
    }

    /// <summary>
    /// Runs the dispatcher and routes its response message. Runs outside the
    /// actor turn, so it MUST NOT touch <see cref="Actor.StateManager"/>. All
    /// failures are logged and surfaced as activity events.
    /// </summary>
    private async Task RunDispatchAsync(
        Message message, PromptAssemblyContext context, CancellationToken cancellationToken)
    {
        try
        {
            var response = await executionDispatcher.DispatchAsync(message, context, cancellationToken);
            if (response is null)
            {
                _logger.LogInformation(
                    "Dispatcher returned no response for conversation {ConversationId}; nothing to route.",
                    message.ConversationId);
                return;
            }

            var routingResult = await messageRouter.RouteAsync(response, cancellationToken);
            if (!routingResult.IsSuccess)
            {
                _logger.LogWarning(
                    "Failed to route dispatcher response for conversation {ConversationId}: {Error}",
                    message.ConversationId, routingResult.Error);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation(
                "Dispatch cancelled for actor {ActorId} conversation {ConversationId}.",
                Id.GetId(), message.ConversationId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Dispatch failed for actor {ActorId} conversation {ConversationId}.",
                Id.GetId(), message.ConversationId);

            await EmitActivityEventAsync(
                ActivityEventType.ErrorOccurred,
                $"Dispatch failed: {ex.Message}",
                CancellationToken.None,
                correlationId: message.ConversationId);
        }
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

        await EmitActivityEventAsync(ActivityEventType.StateChanged,
            "State changed from Active to Suspended",
            cancellationToken,
            details: JsonSerializer.SerializeToElement(new { from = "Active", to = "Suspended" }));
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
    /// Determines whether this agent is a clone by checking for a stored <see cref="CloneIdentity"/>.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns><c>true</c> if this agent is a clone; otherwise <c>false</c>.</returns>
    internal async Task<bool> IsCloneAsync(CancellationToken cancellationToken = default)
    {
        var result = await StateManager
            .TryGetStateAsync<CloneIdentity>(StateKeys.CloneIdentity, cancellationToken);
        return result.HasValue;
    }

    /// <summary>
    /// Gets the clone identity of this agent, if it is a clone.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The <see cref="CloneIdentity"/> if this agent is a clone; otherwise <c>null</c>.</returns>
    internal async Task<CloneIdentity?> GetCloneIdentityAsync(CancellationToken cancellationToken = default)
    {
        var result = await StateManager
            .TryGetStateAsync<CloneIdentity>(StateKeys.CloneIdentity, cancellationToken);
        return result.HasValue ? result.Value : null;
    }

    /// <summary>
    /// Gets the parent agent ID if this agent is a clone, used for cost attribution.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The parent agent ID if this is a clone; otherwise <c>null</c>.</returns>
    internal async Task<string?> GetCostAttributionTargetAsync(CancellationToken cancellationToken = default)
    {
        var identity = await GetCloneIdentityAsync(cancellationToken);
        return identity?.ParentAgentId;
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
        string? correlationId = null,
        decimal? cost = null)
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
                correlationId,
                cost);

            await activityEventBus.PublishAsync(activityEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit activity event {EventType} for actor {ActorId}.",
                eventType, Id.GetId());
        }
    }

    /// <inheritdoc />
    public async Task<AgentMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        var model = await StateManager.TryGetStateAsync<string>(StateKeys.AgentModel, cancellationToken);
        var specialty = await StateManager.TryGetStateAsync<string>(StateKeys.AgentSpecialty, cancellationToken);
        var enabled = await StateManager.TryGetStateAsync<bool>(StateKeys.AgentEnabled, cancellationToken);
        var executionMode = await StateManager.TryGetStateAsync<AgentExecutionMode>(StateKeys.AgentExecutionMode, cancellationToken);
        var parentUnit = await StateManager.TryGetStateAsync<string>(StateKeys.AgentParentUnit, cancellationToken);

        return new AgentMetadata(
            Model: model.HasValue ? model.Value : null,
            Specialty: specialty.HasValue ? specialty.Value : null,
            Enabled: enabled.HasValue ? enabled.Value : null,
            ExecutionMode: executionMode.HasValue ? executionMode.Value : null,
            ParentUnit: parentUnit.HasValue ? parentUnit.Value : null);
    }

    /// <inheritdoc />
    public async Task SetMetadataAsync(AgentMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var writtenFields = new List<string>();

        if (metadata.Model is not null)
        {
            await StateManager.SetStateAsync(StateKeys.AgentModel, metadata.Model, cancellationToken);
            writtenFields.Add(nameof(metadata.Model));
        }

        if (metadata.Specialty is not null)
        {
            await StateManager.SetStateAsync(StateKeys.AgentSpecialty, metadata.Specialty, cancellationToken);
            writtenFields.Add(nameof(metadata.Specialty));
        }

        if (metadata.Enabled is not null)
        {
            await StateManager.SetStateAsync(StateKeys.AgentEnabled, metadata.Enabled.Value, cancellationToken);
            writtenFields.Add(nameof(metadata.Enabled));
        }

        if (metadata.ExecutionMode is not null)
        {
            await StateManager.SetStateAsync(StateKeys.AgentExecutionMode, metadata.ExecutionMode.Value, cancellationToken);
            writtenFields.Add(nameof(metadata.ExecutionMode));
        }

        if (metadata.ParentUnit is not null)
        {
            await StateManager.SetStateAsync(StateKeys.AgentParentUnit, metadata.ParentUnit, cancellationToken);
            writtenFields.Add(nameof(metadata.ParentUnit));
        }

        if (writtenFields.Count == 0)
        {
            _logger.LogDebug(
                "Agent {ActorId} SetMetadataAsync called with no fields; nothing to emit.",
                Id.GetId());
            return;
        }

        _logger.LogInformation(
            "Agent {ActorId} metadata updated: {Fields}",
            Id.GetId(), string.Join(",", writtenFields));

        await EmitActivityEventAsync(ActivityEventType.StateChanged,
            $"Agent metadata updated: {string.Join(", ", writtenFields)}",
            cancellationToken,
            details: JsonSerializer.SerializeToElement(new
            {
                action = "AgentMetadataUpdated",
                fields = writtenFields,
                model = metadata.Model,
                specialty = metadata.Specialty,
                enabled = metadata.Enabled,
                executionMode = metadata.ExecutionMode?.ToString(),
                parentUnit = metadata.ParentUnit,
            }));
    }

    /// <summary>
    /// Clears the agent's parent-unit pointer. Used by the unit's unassign
    /// endpoint alongside removal from the unit's <c>members</c> list, so
    /// <see cref="AgentMetadata.ParentUnit"/> and the unit's member list stay
    /// in sync. Separated from <see cref="SetMetadataAsync"/> because the
    /// partial-patch semantics there treat <c>null</c> as "leave untouched."
    /// </summary>
    public async Task ClearParentUnitAsync(CancellationToken cancellationToken = default)
    {
        await StateManager.RemoveStateAsync(StateKeys.AgentParentUnit, cancellationToken);

        _logger.LogInformation("Agent {ActorId} parent-unit pointer cleared.", Id.GetId());

        await EmitActivityEventAsync(ActivityEventType.StateChanged,
            "Agent parent-unit cleared",
            cancellationToken,
            details: JsonSerializer.SerializeToElement(new
            {
                action = "AgentParentUnitCleared",
            }));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> GetSkillsAsync(CancellationToken cancellationToken = default)
    {
        var result = await StateManager.TryGetStateAsync<List<string>>(StateKeys.AgentSkills, cancellationToken);
        return result.HasValue ? result.Value.AsReadOnly() : [];
    }

    /// <inheritdoc />
    public async Task SetSkillsAsync(IReadOnlyList<string> skills, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skills);

        // Canonicalise: drop null / whitespace entries, collapse duplicates,
        // sort. Ordering is semantically meaningless — the list is a set —
        // but a stable order makes diffs in logs and activity events
        // predictable.
        var normalised = skills
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        await StateManager.SetStateAsync(StateKeys.AgentSkills, normalised, cancellationToken);

        _logger.LogInformation(
            "Agent {ActorId} skills replaced. Count: {Count}", Id.GetId(), normalised.Count);

        await EmitActivityEventAsync(ActivityEventType.StateChanged,
            $"Agent skills replaced: {normalised.Count} skill(s).",
            cancellationToken,
            details: JsonSerializer.SerializeToElement(new
            {
                action = "AgentSkillsReplaced",
                count = normalised.Count,
                skills = normalised,
            }));
    }

    /// <summary>
    /// Emits a <see cref="ActivityEventType.CostIncurred"/> event for this agent's execution costs.
    /// </summary>
    /// <param name="cost">The cost incurred.</param>
    /// <param name="model">The LLM model name.</param>
    /// <param name="inputTokens">Number of input tokens consumed.</param>
    /// <param name="outputTokens">Number of output tokens produced.</param>
    /// <param name="source">
    /// Whether this cost was incurred while doing normal agent work
    /// (<see cref="Core.Costs.CostSource.Work"/>) or inside the initiative
    /// / reflection loop (<see cref="Core.Costs.CostSource.Initiative"/>).
    /// The caller must know which one it is — AgentActor has no reliable way
    /// to infer it after the fact, which is exactly the reason #101 moved
    /// classification out of the UI.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    internal async Task EmitCostIncurredAsync(
        decimal cost,
        string model,
        int inputTokens,
        int outputTokens,
        Core.Costs.CostSource source,
        CancellationToken cancellationToken = default)
    {
        var costAttributionTarget = await GetCostAttributionTargetAsync(cancellationToken);
        var details = JsonSerializer.SerializeToElement(new
        {
            model,
            inputTokens,
            outputTokens,
            parentAgentId = costAttributionTarget,
            costSource = source.ToString(),
        });

        await EmitActivityEventAsync(
            ActivityEventType.CostIncurred,
            $"Cost incurred: {cost:C} ({model}, {inputTokens} in / {outputTokens} out)",
            cancellationToken,
            details: details,
            cost: cost);
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

    /// <summary>
    /// Records an observation for this agent. Observations are appended to a bounded
    /// in-state channel and are drained on the next initiative reminder tick.
    /// Emits an <see cref="ActivityEventType.InitiativeTriggered"/> activity event
    /// so observers can see that the agent was poked, even when Tier 1 ignores it.
    /// </summary>
    /// <param name="observation">The observation payload.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    public async Task RecordObservationAsync(JsonElement observation, CancellationToken ct)
    {
        var existing = await StateManager
            .TryGetStateAsync<List<JsonElement>>(StateKeys.ObservationChannel, ct);

        var list = existing.HasValue ? existing.Value : new List<JsonElement>();
        list.Add(observation);

        // Bound the list to the most recent MaxObservationChannelEntries.
        if (list.Count > MaxObservationChannelEntries)
        {
            list.RemoveRange(0, list.Count - MaxObservationChannelEntries);
        }

        await StateManager.SetStateAsync(StateKeys.ObservationChannel, list, ct);

        await RegisterInitiativeReminderAsync(ct);

        var summary = SummarizeObservation(observation);
        await EmitActivityEventAsync(
            ActivityEventType.InitiativeTriggered,
            $"Observation recorded: {summary}",
            ct);
    }

    /// <inheritdoc />
    public async Task ReceiveReminderAsync(string reminderName, byte[] state, TimeSpan dueTime, TimeSpan period)
    {
        _ = state;
        _ = dueTime;
        _ = period;

        switch (reminderName)
        {
            case InitiativeReminderName:
                await RunInitiativeCheckAsync(CancellationToken.None);
                break;
            default:
                _logger.LogDebug("Actor {ActorId} ignored unknown reminder {ReminderName}",
                    Id.GetId(), reminderName);
                break;
        }
    }

    /// <summary>
    /// Drains the observation channel through <see cref="IInitiativeEngine"/> and, if
    /// Tier 2 decides to act, emits a <see cref="ActivityEventType.ReflectionCompleted"/>
    /// activity event. The observation list is cleared only on a successful engine call.
    /// </summary>
    private async Task RunInitiativeCheckAsync(CancellationToken ct)
    {
        var existing = await StateManager
            .TryGetStateAsync<List<JsonElement>>(StateKeys.ObservationChannel, ct);

        if (!existing.HasValue || existing.Value.Count == 0)
        {
            return;
        }

        var observations = existing.Value;
        var agentId = Id.GetId();

        ReflectionOutcome? outcome;
        try
        {
            outcome = await initiativeEngine.ProcessObservationsAsync(agentId, observations, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Initiative engine threw for actor {ActorId}; retaining observations for next tick.",
                agentId);
            return;
        }

        // Only clear observations after a successful engine call.
        await StateManager.SetStateAsync(StateKeys.ObservationChannel, new List<JsonElement>(), ct);

        if (outcome is null || !outcome.ShouldAct)
        {
            return;
        }

        var details = JsonSerializer.SerializeToElement(new
        {
            actionType = outcome.ActionType,
            reasoning = outcome.Reasoning,
            actionPayload = outcome.ActionPayload,
        });

        await EmitActivityEventAsync(
            ActivityEventType.ReflectionCompleted,
            $"Reflection decided to act: {outcome.ActionType ?? "(unknown)"}",
            ct,
            details: details);

        // TODO(#69 follow-up): dispatch outcome.ActionType with outcome.ActionPayload via MessageRouter
    }

    /// <summary>
    /// Lazily registers the Dapr reminder that drives periodic initiative checks.
    /// The registration is idempotent — the persisted
    /// <see cref="StateKeys.InitiativeReminderRegistered"/> flag prevents duplicate work.
    /// The reminder period is derived from <see cref="Tier2Config.MaxCallsPerHour"/>.
    /// </summary>
    private async Task RegisterInitiativeReminderAsync(CancellationToken ct)
    {
        var registered = await StateManager
            .TryGetStateAsync<bool>(StateKeys.InitiativeReminderRegistered, ct);

        if (registered.HasValue && registered.Value)
        {
            return;
        }

        var maxCallsPerHour = 5;
        try
        {
            var policy = await policyStore.GetPolicyAsync($"agent:{Id.GetId()}", ct);
            if (policy.Tier2 is not null)
            {
                maxCallsPerHour = policy.Tier2.MaxCallsPerHour;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Could not read initiative policy for {ActorId}; using default reminder period.",
                Id.GetId());
        }

        var period = TimeSpan.FromHours(1.0 / Math.Max(1, maxCallsPerHour));

        try
        {
            await RegisterReminderAsync(InitiativeReminderName, state: null, dueTime: period, period: period);
            await StateManager.SetStateAsync(StateKeys.InitiativeReminderRegistered, true, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to register initiative reminder for actor {ActorId}.",
                Id.GetId());
        }
    }

    /// <summary>
    /// Produces a short, human-readable summary for an observation. If the observation is
    /// an object with a <c>summary</c> string property, that value is used. Otherwise the
    /// raw JSON is truncated to 200 characters.
    /// </summary>
    private static string SummarizeObservation(JsonElement observation)
    {
        if (observation.ValueKind == JsonValueKind.Object &&
            observation.TryGetProperty("summary", out var summary) &&
            summary.ValueKind == JsonValueKind.String)
        {
            return summary.GetString() ?? observation.ToString();
        }

        var raw = observation.ToString();
        return raw.Length <= 200 ? raw : raw[..200];
    }
}