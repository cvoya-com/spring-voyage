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
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Routing;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
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
    IReflectionActionHandlerRegistry reflectionActionHandlers,
    IUnitPolicyEnforcer unitPolicyEnforcer,
    IAgentInitiativeEvaluator initiativeEvaluator,
    ILoggerFactory loggerFactory,
    IExpertiseSeedProvider? expertiseSeedProvider = null,
    IActorProxyFactory? actorProxyFactory = null) : Actor(host), IAgentActor, IRemindable
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

    /// <summary>
    /// Seeds the agent's expertise from its <c>AgentDefinition</c> YAML on
    /// first activation (#488). Precedence rule: actor state is authoritative
    /// — the seed only applies when no expertise has been persisted to actor
    /// state yet (<see cref="StateKeys.AgentExpertise"/> unset). Once an
    /// operator has PUT an expertise list (even an empty one), the actor
    /// never re-seeds from YAML so runtime edits survive process restarts.
    /// See <c>docs/architecture/units.md § Seeding from YAML</c>.
    /// </summary>
    /// <remarks>
    /// Failures in seeding are non-fatal: the actor still activates and the
    /// operator can push the seed later via
    /// <c>PUT /api/v1/agents/{id}/expertise</c>. The warning is logged so
    /// persistent seeding failures are visible in the observability pipeline.
    /// </remarks>
    protected override async Task OnActivateAsync()
    {
        await SeedExpertiseFromDefinitionAsync(CancellationToken.None);
        await base.OnActivateAsync();
    }

    private async Task SeedExpertiseFromDefinitionAsync(CancellationToken cancellationToken)
    {
        if (expertiseSeedProvider is null)
        {
            return;
        }

        try
        {
            var existing = await StateManager
                .TryGetStateAsync<List<ExpertiseDomain>>(StateKeys.AgentExpertise, cancellationToken);

            // Actor state wins — if ANY value (including an empty list) was
            // persisted through SetExpertiseAsync, the operator's runtime
            // edit is preserved across activations.
            if (existing.HasValue)
            {
                return;
            }

            var seed = await expertiseSeedProvider.GetAgentSeedAsync(Id.GetId(), cancellationToken);
            if (seed is null || seed.Count == 0)
            {
                return;
            }

            await SetExpertiseAsync(seed.ToArray(), cancellationToken);

            _logger.LogInformation(
                "Agent {ActorId} seeded expertise from AgentDefinition YAML. Domain count: {Count}",
                Id.GetId(), seed.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Agent {ActorId} failed to seed expertise from AgentDefinition; activation proceeding with empty expertise.",
                Id.GetId());
        }
    }

    /// <inheritdoc />
    public async Task<Message?> ReceiveAsync(Message message, CancellationToken cancellationToken = default)
    {
        try
        {
            // correlationId carries the conversation id so the conversation
            // projection (IConversationQueryService, #452) can group every
            // thread-related event. Null when the caller didn't supply a
            // conversation id — still acceptable on standalone messages like
            // StatusQuery / HealthCheck.
            await EmitActivityEventAsync(ActivityEventType.MessageReceived,
                $"Received {message.Type} message {message.Id} from {message.From}",
                cancellationToken,
                correlationId: message.ConversationId);

            return message.Type switch
            {
                MessageType.Cancel => await HandleCancelAsync(message, cancellationToken),
                MessageType.StatusQuery => await HandleStatusQueryAsync(message, cancellationToken),
                MessageType.HealthCheck => await HandleHealthCheckAsync(message, cancellationToken),
                MessageType.PolicyUpdate => await HandlePolicyUpdateAsync(message, cancellationToken),
                MessageType.Amendment => await HandleAmendmentAsync(message, cancellationToken),
                MessageType.Domain => await HandleDomainMessageAsync(message, cancellationToken),
                _ => throw new CallerValidationException(
                    CallerValidationCodes.UnknownMessageType,
                    $"Unknown message type: {message.Type}")
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
    /// Handles a mid-flight amendment message (#142). Amendments are a
    /// supervisor-originated instruction that nudges a live agent without
    /// resetting its context. The flow is:
    /// <list type="bullet">
    /// <item><description>
    /// Validate the sender — only the agent itself or a unit the agent is a
    /// member of may amend. Anyone else is rejected with
    /// <see cref="ActivityEventType.AmendmentRejected"/>.
    /// </description></item>
    /// <item><description>
    /// Disabled memberships silently log-and-drop: we emit an
    /// <see cref="ActivityEventType.AmendmentRejected"/> event for
    /// observability but return an ack so senders cannot use amendments to
    /// probe the enabled flag.
    /// </description></item>
    /// <item><description>
    /// If no turn is in progress, the amendment is queued anyway — the
    /// next turn will pick it up — and no cancellation fires.
    /// </description></item>
    /// <item><description>
    /// For <see cref="AmendmentPriority.StopAndWait"/>, cancel the active
    /// work token, suspend the active conversation, and set the
    /// <see cref="StateKeys.AgentPaused"/> flag.
    /// </description></item>
    /// </list>
    /// </summary>
    private async Task<Message?> HandleAmendmentAsync(Message message, CancellationToken cancellationToken)
    {
        AmendmentPayload? payload;
        try
        {
            payload = message.Payload.Deserialize<AmendmentPayload>();
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Text))
        {
            await EmitAmendmentRejectedAsync(message, "MalformedPayload",
                "Amendment payload missing required Text field.", cancellationToken);
            return CreateAckResponse(message);
        }

        // Authorisation: amendments are only accepted from the agent itself
        // or from a unit that contains this agent. Use the existing
        // membership-repository seam (C2b-1) — we already depend on it for
        // effective-metadata lookup.
        var allowed = await IsAmendmentSenderAllowedAsync(message.From, cancellationToken);
        if (!allowed.Allowed)
        {
            await EmitAmendmentRejectedAsync(
                message,
                allowed.Reason ?? "Rejected",
                allowed.Detail ?? "Amendment rejected.",
                cancellationToken);
            return CreateAckResponse(message);
        }

        // Disabled memberships: log-and-drop. Returning an ack keeps the
        // sender from using the amendment channel as an enabled-flag probe.
        if (allowed.DisabledMembership)
        {
            _logger.LogInformation(
                "Actor {ActorId} dropping amendment {MessageId} from {Sender}: membership Enabled=false.",
                Id.GetId(), message.Id, message.From);

            await EmitAmendmentRejectedAsync(message, "MembershipDisabled",
                "Amendment from a unit in which the agent is disabled.", cancellationToken);
            return CreateAckResponse(message);
        }

        var pending = new PendingAmendment(
            Id: message.Id,
            From: message.From,
            Text: payload.Text,
            Priority: payload.Priority,
            CorrelationId: payload.CorrelationId ?? message.ConversationId,
            ReceivedAt: DateTimeOffset.UtcNow);

        await EnqueueAmendmentAsync(pending, cancellationToken);

        await EmitActivityEventAsync(ActivityEventType.AmendmentReceived,
            $"Amendment accepted from {message.From.Scheme}://{message.From.Path} at priority {payload.Priority}.",
            cancellationToken,
            details: JsonSerializer.SerializeToElement(new
            {
                messageId = message.Id,
                priority = payload.Priority.ToString(),
                correlationId = pending.CorrelationId,
                text = payload.Text,
            }),
            correlationId: pending.CorrelationId);

        if (payload.Priority == AmendmentPriority.StopAndWait)
        {
            await ApplyStopAndWaitAsync(cancellationToken);
        }

        return CreateAckResponse(message);
    }

    /// <summary>
    /// Decides whether <paramref name="sender"/> is permitted to amend this
    /// agent. Returns structured output so the caller can distinguish between
    /// rejection categories when emitting activity events.
    /// </summary>
    private async Task<AmendmentAuthorisation> IsAmendmentSenderAllowedAsync(
        Address sender, CancellationToken cancellationToken)
    {
        // Self-amendment: the agent addresses itself. Always allowed — this
        // is how a human operator signing on as the agent can push a nudge.
        if (string.Equals(sender.Scheme, "agent", StringComparison.Ordinal) &&
            string.Equals(sender.Path, Id.GetId(), StringComparison.Ordinal))
        {
            return AmendmentAuthorisation.Allow();
        }

        // Only parent units may amend. All other schemes are rejected.
        if (!string.Equals(sender.Scheme, "unit", StringComparison.Ordinal))
        {
            return AmendmentAuthorisation.Reject("NotAMember",
                $"Sender {sender.Scheme}://{sender.Path} is not a parent unit or the agent itself.");
        }

        UnitMembership? membership;
        try
        {
            membership = await membershipRepository
                .GetAsync(unitId: sender.Path, agentAddress: Id.GetId(), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Repository failure is treated as reject so a stale/broken
            // membership store cannot widen the amendment surface.
            _logger.LogWarning(ex,
                "Membership lookup failed evaluating amendment sender {Sender} for agent {AgentId}; rejecting.",
                sender, Id.GetId());
            return AmendmentAuthorisation.Reject("MembershipLookupFailed", ex.Message);
        }

        if (membership is null)
        {
            return AmendmentAuthorisation.Reject("NotAMember",
                $"Agent is not a member of unit '{sender.Path}'.");
        }

        return new AmendmentAuthorisation(
            Allowed: true,
            DisabledMembership: membership.Enabled == false,
            Reason: null,
            Detail: null);
    }

    private async Task EnqueueAmendmentAsync(PendingAmendment pending, CancellationToken cancellationToken)
    {
        var existing = await StateManager
            .TryGetStateAsync<List<PendingAmendment>>(StateKeys.AgentPendingAmendments, cancellationToken);

        var list = existing.HasValue ? existing.Value : new List<PendingAmendment>();
        list.Add(pending);

        await StateManager.SetStateAsync(StateKeys.AgentPendingAmendments, list, cancellationToken);
    }

    private async Task ApplyStopAndWaitAsync(CancellationToken cancellationToken)
    {
        if (_activeWorkCancellation is not null)
        {
            await _activeWorkCancellation.CancelAsync();
            _activeWorkCancellation.Dispose();
            _activeWorkCancellation = null;
        }

        await StateManager.SetStateAsync(StateKeys.AgentPaused, true, cancellationToken);

        await EmitActivityEventAsync(ActivityEventType.StateChanged,
            "State changed to Paused awaiting clarification.",
            cancellationToken,
            details: JsonSerializer.SerializeToElement(new { from = "Active", to = "Paused" }));
    }

    /// <summary>
    /// Clears the <see cref="StateKeys.AgentPaused"/> flag. The next domain
    /// message (or an explicit resume endpoint in a future PR) will resume
    /// normal processing. Exposed for tests and for future resume APIs.
    /// </summary>
    internal Task ResumeFromPauseAsync(CancellationToken cancellationToken = default)
        => StateManager.TryRemoveStateAsync(StateKeys.AgentPaused, cancellationToken);

    private Task EmitAmendmentRejectedAsync(
        Message message, string reason, string detail, CancellationToken ct)
    {
        var details = JsonSerializer.SerializeToElement(new
        {
            reason,
            detail,
            sender = new { scheme = message.From.Scheme, path = message.From.Path },
            messageId = message.Id,
        });

        return EmitActivityEventAsync(
            ActivityEventType.AmendmentRejected,
            $"Amendment rejected from {message.From.Scheme}://{message.From.Path}: {reason}",
            ct,
            details: details,
            correlationId: message.ConversationId);
    }

    /// <summary>
    /// Internal result type for amendment-sender authorisation. Kept as a
    /// local record so it never leaks into the public API.
    /// </summary>
    private readonly record struct AmendmentAuthorisation(
        bool Allowed,
        bool DisabledMembership,
        string? Reason,
        string? Detail)
    {
        public static AmendmentAuthorisation Allow() => new(true, false, null, null);

        public static AmendmentAuthorisation Reject(string reason, string detail) =>
            new(false, false, reason, detail);
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
            ?? throw new CallerValidationException(
                CallerValidationCodes.MissingConversationId,
                "Domain messages must have a ConversationId");

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

        // Unit-policy enforcement on the dispatch path (#247 / #248 / #249).
        // Model and cost caps refuse the turn when the unit would not permit
        // it; execution-mode is coerced (forced mode) or denied (outside the
        // allow-list). Silently swapping a model would break user expectations,
        // so deny outcomes become a DecisionMade "BlockedByUnitPolicy" event
        // and the message is acked without dispatch. Policy misconfiguration
        // must never swallow an exception — a denying decision is surfaced to
        // the agent as an activity event so operators can trace it.
        (effective, var policyVerdict) = await ApplyUnitPoliciesAsync(effective, cancellationToken);
        if (policyVerdict is not null)
        {
            await EmitActivityEventAsync(ActivityEventType.DecisionMade,
                $"Skipped message {message.Id} from {message.From}: {policyVerdict.Summary}.",
                cancellationToken,
                details: JsonSerializer.SerializeToElement(new
                {
                    decision = policyVerdict.DecisionTag,
                    dimension = policyVerdict.Dimension,
                    reason = policyVerdict.Decision.Reason,
                    denyingUnitId = policyVerdict.Decision.DenyingUnitId,
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

        // Surface any queued supervisor amendments (#142) so the dispatcher
        // can fold them into the next model call. The actor is not in a
        // position to decide how the dispatcher materialises them (inline
        // system message, sidecar tool-call, etc.) — that is the dispatcher's
        // concern. The actor's contract is simply "everything queued so far
        // is visible on the next dispatch".
        var pendingAmendments = await StateManager
            .TryGetStateAsync<List<PendingAmendment>>(StateKeys.AgentPendingAmendments, cancellationToken);

        IReadOnlyList<PendingAmendment>? amendments = pendingAmendments.HasValue && pendingAmendments.Value.Count > 0
            ? pendingAmendments.Value
            : null;

        return new PromptAssemblyContext(
            Members: [],
            Policies: null,
            Skills: skills,
            PriorMessages: channel.Messages.ToList(),
            LastCheckpoint: null,
            AgentInstructions: definition?.Instructions,
            EffectiveMetadata: effective,
            PendingAmendments: amendments);
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
    /// Carries a unit-policy denial across the agent-dispatch plumbing.
    /// Composed by <see cref="ApplyUnitPoliciesAsync"/> and consumed by
    /// <see cref="HandleDomainMessageAsync"/> to emit a structured
    /// <c>DecisionMade</c> activity event without threading raw
    /// <see cref="PolicyDecision"/> values into every caller.
    /// </summary>
    internal sealed record PolicyVerdict(
        string Dimension,
        string DecisionTag,
        string Summary,
        PolicyDecision Decision);

    /// <summary>
    /// Applies unit-level policy dimensions (#247 model, #248 cost, #249
    /// execution mode) to the per-turn effective metadata. Returns the
    /// (possibly coerced) metadata plus a non-<c>null</c>
    /// <see cref="PolicyVerdict"/> when the dispatch must be refused.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Model and cost deny outcomes refuse the turn — silently swapping a
    /// model mid-turn would break user expectations, and continuing past a
    /// cost cap defeats the cap's purpose. Execution-mode coercion by a
    /// forcing unit is treated as an allow (the call proceeds under the
    /// forced mode); an allow-list miss refuses the turn.
    /// </para>
    /// <para>
    /// Cost evaluation uses a projected cost of <c>0</c>: this seam does not
    /// know the prompt size yet. It is still meaningful because
    /// <see cref="DefaultUnitPolicyEnforcer.EvaluateCostAsync"/> sums the
    /// agent's existing window spend — a unit that already exceeded its hour
    /// / day cap will deny the turn before it runs.
    /// </para>
    /// </remarks>
    internal virtual async Task<(AgentMetadata Effective, PolicyVerdict? Verdict)> ApplyUnitPoliciesAsync(
        AgentMetadata effective, CancellationToken cancellationToken)
    {
        var agentId = Id.GetId();

        // Model caps (#247): deny on block-list hit / whitelist miss. Null
        // model means the downstream dispatcher picks a default — no cap
        // applies at this seam.
        if (!string.IsNullOrWhiteSpace(effective.Model))
        {
            PolicyDecision modelDecision;
            try
            {
                modelDecision = await unitPolicyEnforcer.EvaluateModelAsync(
                    agentId, effective.Model, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Unit policy enforcer threw evaluating model '{Model}' for agent {AgentId}; allowing to avoid losing the turn.",
                    effective.Model, agentId);
                modelDecision = PolicyDecision.Allowed;
            }

            if (!modelDecision.IsAllowed)
            {
                return (effective, new PolicyVerdict(
                    Dimension: "model",
                    DecisionTag: "BlockedByUnitModelPolicy",
                    Summary: modelDecision.Reason ?? $"model '{effective.Model}' denied",
                    Decision: modelDecision));
            }
        }

        // Cost caps (#248): zero projected cost — the enforcer still checks
        // whether the current rolling-window sum has already exceeded the cap.
        PolicyDecision costDecision;
        try
        {
            costDecision = await unitPolicyEnforcer.EvaluateCostAsync(
                agentId, 0m, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Unit policy enforcer threw evaluating cost for agent {AgentId}; allowing to avoid losing the turn.",
                agentId);
            costDecision = PolicyDecision.Allowed;
        }

        if (!costDecision.IsAllowed)
        {
            return (effective, new PolicyVerdict(
                Dimension: "cost",
                DecisionTag: "BlockedByUnitCostPolicy",
                Summary: costDecision.Reason ?? "cost cap exceeded",
                Decision: costDecision));
        }

        // Execution mode (#249): resolve — coercion by a forcing unit wins,
        // otherwise a non-matching allow-list denies.
        var requestedMode = effective.ExecutionMode ?? AgentExecutionMode.Auto;
        ExecutionModeResolution resolution;
        try
        {
            resolution = await unitPolicyEnforcer.ResolveExecutionModeAsync(
                agentId, requestedMode, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Unit policy enforcer threw evaluating execution mode for agent {AgentId}; allowing to avoid losing the turn.",
                agentId);
            resolution = ExecutionModeResolution.AllowAsIs(requestedMode);
        }

        if (!resolution.Decision.IsAllowed)
        {
            return (effective, new PolicyVerdict(
                Dimension: "executionMode",
                DecisionTag: "BlockedByUnitExecutionModePolicy",
                Summary: resolution.Decision.Reason ?? $"execution mode '{requestedMode}' denied",
                Decision: resolution.Decision));
        }

        if (resolution.Mode != requestedMode)
        {
            effective = effective with { ExecutionMode = resolution.Mode };
        }

        return (effective, null);
    }

    /// <summary>
    /// Runs the dispatcher and routes its response message. Runs outside the
    /// actor turn, so it MUST NOT touch <see cref="Actor.StateManager"/>. All
    /// failures are logged and surfaced as activity events. When the dispatch
    /// terminates abnormally (non-zero container exit per #1036, or an
    /// exception inside the dispatcher), the active conversation is cleared
    /// via a Dapr self-call to <see cref="ClearActiveConversationAsync"/> so
    /// the state mutation runs on the actor turn — see #1036/#1038 for why a
    /// failed dispatch must not leave the agent permanently active.
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

            var dispatchExit = TryReadDispatchExit(response);
            if (dispatchExit is { ExitCode: not 0 } failure)
            {
                _logger.LogWarning(
                    "Dispatch for actor {ActorId} conversation {ConversationId} exited with code {ExitCode}: {StdErrFirstLine}",
                    Id.GetId(), message.ConversationId, failure.ExitCode, failure.StdErrFirstLine);

                var details = JsonSerializer.SerializeToElement(new
                {
                    exitCode = failure.ExitCode,
                    stderr = failure.StdErr,
                    agentId = Id.GetId(),
                    conversationId = message.ConversationId,
                });

                await EmitActivityEventAsync(
                    ActivityEventType.ErrorOccurred,
                    $"Container exit code {failure.ExitCode}: {failure.StdErrFirstLine}",
                    CancellationToken.None,
                    details: details,
                    correlationId: message.ConversationId);

                // Best-effort: still surface the failure to the caller so an
                // upstream agent / human sees the error response. We do this
                // BEFORE clearing the active conversation so the response is
                // ordered correctly in the conversation event log.
                await TryRouteResponseAsync(response, message.ConversationId, cancellationToken);

                await ClearActiveConversationViaSelfAsync(
                    $"dispatch exit code {failure.ExitCode}");
                return;
            }

            await TryRouteResponseAsync(response, message.ConversationId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A cancelled dispatch leaves the active-conversation slot
            // pointing at a dead turn. Without clearing it, the actor
            // refuses every subsequent message in any other conversation
            // (Case 3 in HandleDomainMessageAsync queues them as pending
            // forever) and the agent looks bricked from the user's
            // perspective. The non-zero exit and generic-exception
            // branches below already self-call ClearActiveConversationAsync
            // for exactly this reason; the cancel branch must too.
            // Discovered post-Stage-2 cutover (#1063 / #522 follow-up):
            // a worker-side HttpClient timeout surfaced as
            // OperationCanceledException, the actor logged it but kept
            // the conversation marked Active, and every subsequent
            // user message was queued as pending and never dispatched.
            _logger.LogInformation(
                "Dispatch cancelled for actor {ActorId} conversation {ConversationId}.",
                Id.GetId(), message.ConversationId);

            await ClearActiveConversationViaSelfAsync("dispatch cancelled");
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
                details: JsonSerializer.SerializeToElement(new
                {
                    error = ex.Message,
                    agentId = Id.GetId(),
                    conversationId = message.ConversationId,
                }),
                correlationId: message.ConversationId);

            await ClearActiveConversationViaSelfAsync($"dispatch exception: {ex.GetType().Name}");
        }
    }

    private async Task TryRouteResponseAsync(Message response, string? conversationId, CancellationToken cancellationToken)
    {
        try
        {
            var routingResult = await messageRouter.RouteAsync(response, cancellationToken);
            if (!routingResult.IsSuccess)
            {
                _logger.LogWarning(
                    "Failed to route dispatcher response for conversation {ConversationId}: {Error}",
                    conversationId, routingResult.Error);
            }
        }
        catch (Exception routeEx)
        {
            _logger.LogWarning(routeEx,
                "Routing dispatcher response failed for conversation {ConversationId}.",
                conversationId);
        }
    }

    private readonly record struct DispatchExit(int ExitCode, string? StdErr, string StdErrFirstLine);

    private static DispatchExit? TryReadDispatchExit(Message response)
    {
        try
        {
            if (response.Payload.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!response.Payload.TryGetProperty("ExitCode", out var exitProp) ||
                exitProp.ValueKind != JsonValueKind.Number ||
                !exitProp.TryGetInt32(out var exitCode))
            {
                return null;
            }

            string? stderr = null;
            if (response.Payload.TryGetProperty("Error", out var errProp) &&
                errProp.ValueKind == JsonValueKind.String)
            {
                stderr = errProp.GetString();
            }

            var firstLine = stderr is null
                ? string.Empty
                : stderr.Split('\n', 2)[0].TrimEnd('\r').Trim();

            return new DispatchExit(exitCode, stderr, firstLine);
        }
        catch
        {
            return null;
        }
    }

    private async Task ClearActiveConversationViaSelfAsync(string reason)
    {
        // RunDispatchAsync runs outside the actor turn, so we can't touch
        // StateManager directly — see the docstring. When an actor proxy
        // factory was injected (always the case in production wiring) we
        // self-call the actor through Dapr remoting, which queues the call
        // on the actor's turn queue. In tests where no proxy factory is
        // wired up we fall back to invoking the helper directly; the test
        // harness mocks StateManager so the race the comment is guarding
        // against doesn't apply.
        try
        {
            if (actorProxyFactory is not null)
            {
                var self = actorProxyFactory.CreateActorProxy<IAgentActor>(Id, nameof(AgentActor));
                await self.ClearActiveConversationAsync(reason, CancellationToken.None);
            }
            else
            {
                await ClearActiveConversationAsync(reason, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to clear active conversation for actor {ActorId} after dispatch failure (reason: {Reason}).",
                Id.GetId(), reason);
        }
    }

    /// <inheritdoc />
    public async Task ClearActiveConversationAsync(
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var activeConversation = await StateManager
            .TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, cancellationToken);

        if (!activeConversation.HasValue)
        {
            _logger.LogDebug(
                "Actor {ActorId} ClearActiveConversationAsync called with no active conversation (reason: {Reason}).",
                Id.GetId(), reason);
            return;
        }

        if (_activeWorkCancellation is not null)
        {
            await _activeWorkCancellation.CancelAsync();
            _activeWorkCancellation.Dispose();
            _activeWorkCancellation = null;
        }

        var conversationId = activeConversation.Value.ConversationId;
        await StateManager.TryRemoveStateAsync(StateKeys.ActiveConversation, cancellationToken);

        _logger.LogInformation(
            "Actor {ActorId} cleared active conversation {ConversationId} (reason: {Reason}).",
            Id.GetId(), conversationId, reason);

        await EmitActivityEventAsync(
            ActivityEventType.StateChanged,
            $"State changed from Active to Idle ({reason ?? "unspecified"})",
            cancellationToken,
            details: JsonSerializer.SerializeToElement(new
            {
                from = "Active",
                to = "Idle",
                reason,
                conversationId,
            }),
            correlationId: conversationId);

        await PromoteNextPendingAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task CloseConversationAsync(
        string conversationId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            return;
        }

        var active = await StateManager
            .TryGetStateAsync<ConversationChannel>(StateKeys.ActiveConversation, cancellationToken);

        if (active.HasValue && active.Value.ConversationId == conversationId)
        {
            if (_activeWorkCancellation is not null)
            {
                await _activeWorkCancellation.CancelAsync();
                _activeWorkCancellation.Dispose();
                _activeWorkCancellation = null;
            }

            await StateManager.TryRemoveStateAsync(StateKeys.ActiveConversation, cancellationToken);

            _logger.LogInformation(
                "Actor {ActorId} closed active conversation {ConversationId} (reason: {Reason}).",
                Id.GetId(), conversationId, reason);

            await EmitActivityEventAsync(
                ActivityEventType.ConversationClosed,
                $"Conversation {conversationId} closed ({reason ?? "no reason given"})",
                cancellationToken,
                details: JsonSerializer.SerializeToElement(new
                {
                    conversationId,
                    reason,
                    wasActive = true,
                }),
                correlationId: conversationId);

            await PromoteNextPendingAsync(cancellationToken);
            return;
        }

        var pending = await StateManager
            .TryGetStateAsync<List<ConversationChannel>>(StateKeys.PendingConversations, cancellationToken);

        if (pending.HasValue)
        {
            var list = pending.Value;
            var idx = list.FindIndex(c => c.ConversationId == conversationId);
            if (idx >= 0)
            {
                list.RemoveAt(idx);
                if (list.Count > 0)
                {
                    await StateManager.SetStateAsync(StateKeys.PendingConversations, list, cancellationToken);
                }
                else
                {
                    await StateManager.TryRemoveStateAsync(StateKeys.PendingConversations, cancellationToken);
                }

                _logger.LogInformation(
                    "Actor {ActorId} closed pending conversation {ConversationId} (reason: {Reason}).",
                    Id.GetId(), conversationId, reason);

                await EmitActivityEventAsync(
                    ActivityEventType.ConversationClosed,
                    $"Conversation {conversationId} closed ({reason ?? "no reason given"})",
                    cancellationToken,
                    details: JsonSerializer.SerializeToElement(new
                    {
                        conversationId,
                        reason,
                        wasActive = false,
                    }),
                    correlationId: conversationId);
                return;
            }
        }

        _logger.LogDebug(
            "Actor {ActorId} CloseConversationAsync no-op for unknown conversation {ConversationId} (reason: {Reason}).",
            Id.GetId(), conversationId, reason);
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

        // Promotion alone is not enough: the queued head message must be
        // dispatched, otherwise the actor sits Active-but-idle and the
        // user never gets a response. The first-message path in
        // HandleDomainMessageAsync (Case 1) explicitly schedules
        // RunDispatchAsync after activating; promotion has to do the
        // same. Discovered post-Stage-2 cutover (#1063 / #522 follow-up):
        // after a previous turn was cleared, the queued conversation
        // got "promoted" but never dispatched until the user sent a new
        // message, and even then it stayed in Case 2 (append) which
        // also doesn't dispatch.
        if (next.Messages is { Count: > 0 } messages)
        {
            var head = messages[0];
            try
            {
                var effective = await ResolveEffectiveMetadataAsync(head, cancellationToken);
                if (effective.Enabled == false)
                {
                    _logger.LogInformation(
                        "Actor {ActorId} skipping promoted conversation {ConversationId}: membership Enabled=false.",
                        Id.GetId(), next.ConversationId);
                    return;
                }

                var context = await BuildPromptAssemblyContextAsync(next, effective, cancellationToken);
                PendingDispatchTask = RunDispatchAsync(head, context, _activeWorkCancellation.Token);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Actor {ActorId} failed to dispatch promoted conversation {ConversationId}.",
                    Id.GetId(), next.ConversationId);
            }
        }
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
    public async Task<string[]> GetSkillsAsync(CancellationToken cancellationToken = default)
    {
        var result = await StateManager.TryGetStateAsync<List<string>>(StateKeys.AgentSkills, cancellationToken);
        return result.HasValue ? result.Value.ToArray() : [];
    }

    /// <inheritdoc />
    public async Task SetSkillsAsync(string[] skills, CancellationToken cancellationToken = default)
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

    /// <inheritdoc />
    public async Task<ExpertiseDomain[]> GetExpertiseAsync(CancellationToken cancellationToken = default)
    {
        var result = await StateManager.TryGetStateAsync<List<ExpertiseDomain>>(StateKeys.AgentExpertise, cancellationToken);
        return result.HasValue ? result.Value.ToArray() : Array.Empty<ExpertiseDomain>();
    }

    /// <inheritdoc />
    public async Task SetExpertiseAsync(ExpertiseDomain[] domains, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domains);

        // De-dup by domain name case-insensitively; the last write for a given
        // name wins so a caller can PATCH a level or description by re-listing
        // the same domain.
        var normalised = domains
            .Where(d => !string.IsNullOrWhiteSpace(d.Name))
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToList();

        await StateManager.SetStateAsync(StateKeys.AgentExpertise, normalised, cancellationToken);

        _logger.LogInformation(
            "Agent {ActorId} expertise replaced. Count: {Count}", Id.GetId(), normalised.Count);

        await EmitActivityEventAsync(ActivityEventType.StateChanged,
            $"Agent expertise replaced: {normalised.Count} domain(s).",
            cancellationToken,
            details: JsonSerializer.SerializeToElement(new
            {
                action = "AgentExpertiseReplaced",
                count = normalised.Count,
                domains = normalised.Select(d => new { d.Name, d.Description, Level = d.Level?.ToString() }),
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

        await DispatchReflectionActionAsync(outcome, observations, ct);
    }

    /// <summary>
    /// Translates a <see cref="ReflectionOutcome"/> into a concrete message,
    /// gates it through the initiative-evaluator seam
    /// (<see cref="IAgentInitiativeEvaluator"/>, #415 / PR #550 / #552), and
    /// routes it via <see cref="MessageRouter"/>. The evaluator is the single
    /// source of truth for initiative-specific composed enforcement (unit
    /// initiative-action allow / block list per #250, cost caps per #474,
    /// boundary / hierarchy permissions / cloning as they come online) — this
    /// caller must not re-run those gates.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Decision mapping from the evaluator:
    /// <list type="bullet">
    ///   <item><description><c>Defer</c> — no dispatch, no activity emission (the
    ///     observation has already been drained; the log line covers the
    ///     internal record).</description></item>
    ///   <item><description><c>ActWithConfirmation</c> — the translated message is
    ///     <em>not</em> routed inline; a <c>ReflectionActionProposed</c>
    ///     activity event surfaces the proposal for the parent unit / human
    ///     member to approve. The <c>failedClosed</c> flag is propagated so
    ///     operator dashboards can distinguish "operator asked for confirmation"
    ///     from "a gate could not be evaluated."</description></item>
    ///   <item><description><c>ActAutonomously</c> — the translated message is routed
    ///     and a <c>ReflectionActionDispatched</c> activity event is emitted
    ///     (unchanged from the pre-evaluator Reactive baseline).</description></item>
    /// </list>
    /// </para>
    /// <para>
    /// Upstream, the <see cref="InitiativeEngine"/> has already scrubbed the
    /// outcome against the agent's own <see cref="InitiativePolicy.AllowedActions"/>
    /// / <see cref="InitiativePolicy.BlockedActions"/> slots via
    /// <c>ApplyPolicyToOutcome</c>; the evaluator owns the unit-scoped
    /// initiative-action + cost overlays. The unit-level skill-invocation gate
    /// (#163 / C3) is orthogonal — it governs any skill call, not just
    /// initiative-driven ones — so it stays on the dispatch path.
    /// </para>
    /// </remarks>
    private async Task DispatchReflectionActionAsync(
        ReflectionOutcome outcome,
        IReadOnlyList<JsonElement> signals,
        CancellationToken ct)
    {
        var agentId = Id.GetId();
        var actionType = outcome.ActionType;

        if (string.IsNullOrWhiteSpace(actionType))
        {
            await EmitReflectionSkippedAsync(
                outcome,
                reason: "UnknownActionType",
                detail: "Outcome has no ActionType.",
                ct);
            return;
        }

        // Unit skill policy (#163 / C3) — a cross-cutting skill-allowlist
        // gate that applies to any skill invocation, not just initiative-driven
        // ones. Kept on the dispatch path because the initiative evaluator
        // does not own this concern.
        PolicyDecision unitDecision;
        try
        {
            unitDecision = await unitPolicyEnforcer
                .EvaluateSkillInvocationAsync(agentId, actionType, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Unit policy enforcer threw evaluating action {ActionType} for agent {AgentId}; allowing to avoid losing the action.",
                actionType, agentId);
            unitDecision = PolicyDecision.Allowed;
        }

        if (!unitDecision.IsAllowed)
        {
            await EmitReflectionSkippedAsync(
                outcome,
                reason: "BlockedByUnitPolicy",
                detail: unitDecision.Reason ?? $"Action '{actionType}' blocked by unit policy.",
                ct,
                unitId: unitDecision.DenyingUnitId);
            return;
        }

        var handler = reflectionActionHandlers.Find(actionType);
        if (handler is null)
        {
            await EmitReflectionSkippedAsync(
                outcome,
                reason: "UnknownActionType",
                detail: $"No handler registered for action type '{actionType}'.",
                ct);
            return;
        }

        Message? translated;
        try
        {
            translated = await handler.TranslateAsync(Address, outcome, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Reflection action handler for {ActionType} threw for agent {AgentId}.",
                actionType, agentId);
            await EmitReflectionSkippedAsync(
                outcome,
                reason: "HandlerThrew",
                detail: ex.Message,
                ct);
            return;
        }

        if (translated is null)
        {
            await EmitReflectionSkippedAsync(
                outcome,
                reason: "MalformedPayload",
                detail: $"Handler for '{actionType}' rejected the payload.",
                ct);
            return;
        }

        // Initiative evaluator (#415 / PR #550). Composes the unit-scoped
        // initiative-action policy (#250), cost caps (#474), boundary /
        // hierarchy / cloning gates, and projects the result onto the
        // three-valued decision that drives Reactive / Proactive / Autonomous
        // semantics at runtime. The evaluator re-reads policy on every call,
        // so runtime policy edits propagate here on the next drain.
        var action = new InitiativeAction(
            ActionType: actionType,
            EstimatedCost: 0m,
            IsReversible: true,
            TargetAddress: $"{translated.To.Scheme}://{translated.To.Path}");

        InitiativeEvaluationResult evaluation;
        try
        {
            evaluation = await initiativeEvaluator.EvaluateAsync(
                new InitiativeEvaluationContext(agentId, action, signals),
                ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Evaluator infrastructure failure — fail closed to confirmation.
            _logger.LogWarning(ex,
                "Initiative evaluator threw for agent {AgentId}, action {ActionType}; surfacing as confirmation-required proposal.",
                agentId, actionType);

            await EmitProposalAsync(
                outcome,
                translated,
                reason: $"initiative evaluator threw: {ex.Message}",
                effectiveLevel: null,
                failedClosed: true,
                ct);
            return;
        }

        switch (evaluation.Decision)
        {
            case InitiativeEvaluationDecision.Defer:
                // Issue #552: Defer takes no action and emits no activity
                // event. The internal log line keeps the decision traceable.
                _logger.LogInformation(
                    "Reflection action '{ActionType}' deferred for agent {AgentId}: {Reason}",
                    actionType, agentId, evaluation.Reason ?? "(no reason)");
                return;

            case InitiativeEvaluationDecision.ActWithConfirmation:
                await EmitProposalAsync(
                    outcome,
                    translated,
                    reason: evaluation.Reason,
                    effectiveLevel: evaluation.EffectiveLevel,
                    failedClosed: evaluation.FailedClosed,
                    ct);
                return;

            case InitiativeEvaluationDecision.ActAutonomously:
                // Fall through to inline routing.
                break;

            default:
                _logger.LogWarning(
                    "Initiative evaluator returned unknown decision {Decision} for agent {AgentId}; treating as Defer.",
                    evaluation.Decision, agentId);
                return;
        }

        var routing = await messageRouter.RouteAsync(translated, ct);
        if (!routing.IsSuccess)
        {
            _logger.LogWarning(
                "Routing reflection action {ActionType} for agent {AgentId} failed: {Error}",
                actionType, agentId, routing.Error);
            await EmitReflectionSkippedAsync(
                outcome,
                reason: "RoutingFailed",
                detail: routing.Error?.Message ?? "router returned failure",
                ct);
            return;
        }

        var dispatchDetails = JsonSerializer.SerializeToElement(new
        {
            actionType,
            messageId = translated.Id,
            target = new { scheme = translated.To.Scheme, path = translated.To.Path },
            conversationId = translated.ConversationId,
            effectiveLevel = evaluation.EffectiveLevel.ToString(),
        });

        await EmitActivityEventAsync(
            ActivityEventType.ReflectionActionDispatched,
            $"Reflection action '{actionType}' dispatched to {translated.To.Scheme}://{translated.To.Path}.",
            ct,
            details: dispatchDetails,
            correlationId: translated.ConversationId);
    }

    /// <summary>
    /// Emits a <see cref="ActivityEventType.ReflectionActionProposed"/>
    /// activity event for a reflection action the evaluator flagged as
    /// requiring confirmation. The proposal is surfaced to downstream
    /// observers (dashboards, audit, the private-cloud approval UI) via the
    /// activity bus; the translated message is intentionally <em>not</em>
    /// routed inline so a human / unit owner can approve it first.
    /// </summary>
    private Task EmitProposalAsync(
        ReflectionOutcome outcome,
        Message translated,
        string? reason,
        InitiativeLevel? effectiveLevel,
        bool failedClosed,
        CancellationToken ct)
    {
        var details = JsonSerializer.SerializeToElement(new
        {
            actionType = outcome.ActionType,
            messageId = translated.Id,
            target = new { scheme = translated.To.Scheme, path = translated.To.Path },
            conversationId = translated.ConversationId,
            reason,
            effectiveLevel = effectiveLevel?.ToString(),
            failedClosed,
        });

        var summary = failedClosed
            ? $"Reflection action '{outcome.ActionType}' proposal (fail-closed): {reason ?? "(no reason)"}"
            : $"Reflection action '{outcome.ActionType}' proposal pending confirmation: {reason ?? "(no reason)"}";

        return EmitActivityEventAsync(
            ActivityEventType.ReflectionActionProposed,
            summary,
            ct,
            details: details,
            correlationId: translated.ConversationId);
    }

    private Task EmitReflectionSkippedAsync(
        ReflectionOutcome outcome,
        string reason,
        string detail,
        CancellationToken ct,
        string? unitId = null)
    {
        _logger.LogInformation(
            "Reflection action skipped for actor {ActorId}: {Reason} ({Detail})",
            Id.GetId(), reason, detail);

        var details = JsonSerializer.SerializeToElement(new
        {
            reason,
            detail,
            actionType = outcome.ActionType,
            denyingUnitId = unitId,
        });

        return EmitActivityEventAsync(
            ActivityEventType.ReflectionActionSkipped,
            $"Reflection action skipped: {reason}",
            ct,
            details: details);
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