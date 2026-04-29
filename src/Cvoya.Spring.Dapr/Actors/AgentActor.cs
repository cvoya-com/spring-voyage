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
/// control (highest priority), thread (one per ThreadId), and observation (batched events).
/// The actor never performs long-running work in the actor turn; it dispatches async work externally.
/// </summary>
public class AgentActor(
    ActorHost host,
    IActivityEventBus activityEventBus,
    IAgentObservationCoordinator observationCoordinator,
    IAgentMailboxCoordinator mailboxCoordinator,
    IExecutionDispatcher executionDispatcher,
    MessageRouter messageRouter,
    IAgentDefinitionProvider agentDefinitionProvider,
    IEnumerable<ISkillRegistry> skillRegistries,
    IUnitMembershipRepository membershipRepository,
    IUnitPolicyEnforcer unitPolicyEnforcer,
    IAgentInitiativeEvaluator initiativeEvaluator,
    ILoggerFactory loggerFactory,
    IAgentLifecycleCoordinator lifecycleCoordinator,
    IAgentStateCoordinator stateCoordinator,
    IExpertiseSeedProvider? expertiseSeedProvider = null,
    IActorProxyFactory? actorProxyFactory = null) : Actor(host), IAgentActor, IRemindable
{
    /// <summary>
    /// Name of the Dapr reminder that drives periodic initiative checks.
    /// </summary>
    internal const string InitiativeReminderName = "initiative-check";

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
    /// Runs the actor-activation logic by delegating to
    /// <see cref="IAgentLifecycleCoordinator"/>. The coordinator handles
    /// expertise seeding from <c>AgentDefinition</c> YAML (#488): it applies
    /// the seed only when actor state does not already hold an expertise list
    /// so that runtime operator edits survive process restarts.
    /// See <c>docs/architecture/units.md § Seeding from YAML</c>.
    /// </summary>
    protected override async Task OnActivateAsync()
    {
        await lifecycleCoordinator.ActivateAsync(
            Id.GetId(),
            async ct =>
            {
                var state = await StateManager
                    .TryGetStateAsync<List<ExpertiseDomain>>(StateKeys.AgentExpertise, ct);
                return (state.HasValue, state.HasValue ? state.Value : null);
            },
            ct => expertiseSeedProvider is not null
                ? expertiseSeedProvider.GetAgentSeedAsync(Id.GetId(), ct)
                : Task.FromResult<IReadOnlyList<ExpertiseDomain>?>(null),
            (domains, ct) => SetExpertiseAsync(domains, ct),
            CancellationToken.None);

        await base.OnActivateAsync();
    }

    /// <inheritdoc />
    public async Task<Message?> ReceiveAsync(Message message, CancellationToken cancellationToken = default)
    {
        try
        {
            // correlationId carries the thread id so the thread
            // projection (IThreadQueryService, #452) can group every
            // thread-related event. Null when the caller didn't supply a
            // thread id — still acceptable on standalone messages like
            // StatusQuery / HealthCheck. #1209: persist the message envelope
            // (sender / recipient / payload) on the event so the thread
            // view can render the body inline, not just the summary line.
            await EmitActivityEventAsync(ActivityEventType.MessageReceived,
                $"Received {message.Type} message {message.Id} from {message.From}",
                cancellationToken,
                details: MessageReceivedDetails.Build(message),
                correlationId: message.ThreadId);

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
        _logger.LogInformation("Actor {ActorId} received cancel for thread {ThreadId}",
            Id.GetId(), message.ThreadId);

        if (_activeWorkCancellation is not null)
        {
            await _activeWorkCancellation.CancelAsync();
            _activeWorkCancellation.Dispose();
            _activeWorkCancellation = null;
        }

        var activeConversation = await StateManager
            .TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, cancellationToken)
            ;

        if (activeConversation.HasValue &&
            activeConversation.Value.ThreadId == message.ThreadId)
        {
            await StateManager.TryRemoveStateAsync(StateKeys.ActiveConversation, cancellationToken);

            await EmitActivityEventAsync(ActivityEventType.ThreadCompleted,
                $"Conversation {message.ThreadId} cancelled",
                cancellationToken,
                correlationId: message.ThreadId);

            await PromoteNextPendingAsync(cancellationToken);

            // If no pending thread was promoted, agent returns to Idle.
            var newActive = await StateManager
                .TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, cancellationToken);
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
            .TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, cancellationToken)
            ;
        var pending = await StateManager
            .TryGetStateAsync<List<ThreadChannel>>(StateKeys.PendingConversations, cancellationToken)
            ;

        var statusPayload = JsonSerializer.SerializeToElement(new
        {
            Status = status.ToString(),
            ActiveThreadId = activeConversation.HasValue ? activeConversation.Value.ThreadId : null,
            PendingConversationCount = pending.HasValue ? pending.Value.Count : 0
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
    private Task<Message?> HandleHealthCheckAsync(Message message, CancellationToken cancellationToken)
    {
        _ = cancellationToken; // Unused but kept for signature consistency.
        var healthPayload = JsonSerializer.SerializeToElement(new { Healthy = true });

        Message? response = new Message(
            Guid.NewGuid(),
            Address,
            message.From,
            MessageType.HealthCheck,
            message.ThreadId,
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
    /// work token, suspend the active thread, and set the
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
            CorrelationId: payload.CorrelationId ?? message.ThreadId,
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
            correlationId: message.ThreadId);
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
    /// Handles a domain message by routing it to the appropriate thread channel.
    /// New threads are created if the ThreadId is unseen.
    /// If there is already an active thread for a different ThreadId, the new thread is queued as pending.
    /// When a new thread is activated, the actor kicks off a fire-and-forget dispatch task so the actor turn returns quickly.
    /// </summary>
    private async Task<Message?> HandleDomainMessageAsync(Message message, CancellationToken cancellationToken)
    {
        var threadId = message.ThreadId
            ?? throw new CallerValidationException(
                CallerValidationCodes.MissingThreadId,
                "Domain messages must have a ThreadId");

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
                correlationId: threadId);

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
                correlationId: threadId);

            return CreateAckResponse(message);
        }

        await mailboxCoordinator.HandleDomainMessageAsync(
            agentId: Id.GetId(),
            message: message,
            effective: effective,
            getActiveConversation: ct => GetActiveConversationAsync(ct),
            setActiveConversation: (ch, ct) => StateManager.SetStateAsync(StateKeys.ActiveConversation, ch, ct),
            getPendingList: ct => GetPendingListAsync(ct),
            setPendingList: (list, ct) => StateManager.SetStateAsync(StateKeys.PendingConversations, list, ct),
            activateAndDispatch: async (ch, eff, ct) =>
            {
                _activeWorkCancellation = new CancellationTokenSource();
                var context = await BuildPromptAssemblyContextAsync(ch, eff, ct);
                PendingDispatchTask = RunDispatchAsync(message, context, _activeWorkCancellation.Token);
            },
            emitActivity: EmitActivityEventAsync,
            cancellationToken: cancellationToken);

        return CreateAckResponse(message);
    }

    /// <summary>
    /// Builds the prompt-assembly context for the active thread. Members
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
        ThreadChannel channel,
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
    /// exception inside the dispatcher), the active thread is cleared
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
                    "Dispatcher returned no response for thread {ThreadId}; nothing to route.",
                    message.ThreadId);
                return;
            }

            var dispatchExit = TryReadDispatchExit(response);
            if (dispatchExit is { ExitCode: not 0 } failure)
            {
                _logger.LogWarning(
                    "Dispatch for actor {ActorId} thread {ThreadId} exited with code {ExitCode}: {StdErrFirstLine}",
                    Id.GetId(), message.ThreadId, failure.ExitCode, failure.StdErrFirstLine);

                var details = JsonSerializer.SerializeToElement(new
                {
                    exitCode = failure.ExitCode,
                    stderr = failure.StdErr,
                    agentId = Id.GetId(),
                    threadId = message.ThreadId,
                });

                await EmitActivityEventAsync(
                    ActivityEventType.ErrorOccurred,
                    $"Container exit code {failure.ExitCode}: {failure.StdErrFirstLine}",
                    CancellationToken.None,
                    details: details,
                    correlationId: message.ThreadId);

                // Best-effort: still surface the failure to the caller so an
                // upstream agent / human sees the error response. We do this
                // BEFORE clearing the active thread so the response is
                // ordered correctly in the thread event log.
                await TryRouteResponseAsync(response, message.ThreadId, cancellationToken);

                await ClearActiveConversationViaSelfAsync(
                    $"dispatch exit code {failure.ExitCode}");
                return;
            }

            await TryRouteResponseAsync(response, message.ThreadId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // A cancelled dispatch leaves the active-thread slot
            // pointing at a dead turn. Without clearing it, the actor
            // refuses every subsequent message in any other thread
            // (Case 3 in HandleDomainMessageAsync queues them as pending
            // forever) and the agent looks bricked from the user's
            // perspective. The non-zero exit and generic-exception
            // branches below already self-call ClearActiveConversationAsync
            // for exactly this reason; the cancel branch must too.
            // Discovered post-Stage-2 cutover (#1063 / #522 follow-up):
            // a worker-side HttpClient timeout surfaced as
            // OperationCanceledException, the actor logged it but kept
            // the thread marked Active, and every subsequent
            // user message was queued as pending and never dispatched.
            _logger.LogInformation(
                "Dispatch cancelled for actor {ActorId} thread {ThreadId}.",
                Id.GetId(), message.ThreadId);

            await ClearActiveConversationViaSelfAsync("dispatch cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Dispatch failed for actor {ActorId} thread {ThreadId}.",
                Id.GetId(), message.ThreadId);

            await EmitActivityEventAsync(
                ActivityEventType.ErrorOccurred,
                $"Dispatch failed: {ex.Message}",
                CancellationToken.None,
                details: JsonSerializer.SerializeToElement(new
                {
                    error = ex.Message,
                    agentId = Id.GetId(),
                    threadId = message.ThreadId,
                }),
                correlationId: message.ThreadId);

            await ClearActiveConversationViaSelfAsync($"dispatch exception: {ex.GetType().Name}");
        }
    }

    private async Task TryRouteResponseAsync(Message response, string? threadId, CancellationToken cancellationToken)
    {
        try
        {
            var routingResult = await messageRouter.RouteAsync(response, cancellationToken);
            if (!routingResult.IsSuccess)
            {
                _logger.LogWarning(
                    "Failed to route dispatcher response for thread {ThreadId}: {Error}",
                    threadId, routingResult.Error);
            }
        }
        catch (Exception routeEx)
        {
            _logger.LogWarning(routeEx,
                "Routing dispatcher response failed for thread {ThreadId}.",
                threadId);
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
                "Failed to clear active thread for actor {ActorId} after dispatch failure (reason: {Reason}).",
                Id.GetId(), reason);
        }
    }

    /// <inheritdoc />
    public async Task ClearActiveConversationAsync(
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var activeConversation = await StateManager
            .TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, cancellationToken);

        if (!activeConversation.HasValue)
        {
            _logger.LogDebug(
                "Actor {ActorId} ClearActiveConversationAsync called with no active thread (reason: {Reason}).",
                Id.GetId(), reason);
            return;
        }

        if (_activeWorkCancellation is not null)
        {
            await _activeWorkCancellation.CancelAsync();
            _activeWorkCancellation.Dispose();
            _activeWorkCancellation = null;
        }

        var threadId = activeConversation.Value.ThreadId;
        await StateManager.TryRemoveStateAsync(StateKeys.ActiveConversation, cancellationToken);

        _logger.LogInformation(
            "Actor {ActorId} cleared active thread {ThreadId} (reason: {Reason}).",
            Id.GetId(), threadId, reason);

        await EmitActivityEventAsync(
            ActivityEventType.StateChanged,
            $"State changed from Active to Idle ({reason ?? "unspecified"})",
            cancellationToken,
            details: JsonSerializer.SerializeToElement(new
            {
                from = "Active",
                to = "Idle",
                reason,
                threadId,
            }),
            correlationId: threadId);

        await PromoteNextPendingAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task CloseConversationAsync(
        string threadId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return;
        }

        var active = await StateManager
            .TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, cancellationToken);

        if (active.HasValue && active.Value.ThreadId == threadId)
        {
            if (_activeWorkCancellation is not null)
            {
                await _activeWorkCancellation.CancelAsync();
                _activeWorkCancellation.Dispose();
                _activeWorkCancellation = null;
            }

            await StateManager.TryRemoveStateAsync(StateKeys.ActiveConversation, cancellationToken);

            _logger.LogInformation(
                "Actor {ActorId} closed active thread {ThreadId} (reason: {Reason}).",
                Id.GetId(), threadId, reason);

            await EmitActivityEventAsync(
                ActivityEventType.ThreadClosed,
                $"Thread {threadId} closed ({reason ?? "no reason given"})",
                cancellationToken,
                details: JsonSerializer.SerializeToElement(new
                {
                    threadId,
                    reason,
                    wasActive = true,
                }),
                correlationId: threadId);

            await PromoteNextPendingAsync(cancellationToken);
            return;
        }

        var pending = await StateManager
            .TryGetStateAsync<List<ThreadChannel>>(StateKeys.PendingConversations, cancellationToken);

        if (pending.HasValue)
        {
            var list = pending.Value;
            var idx = list.FindIndex(c => c.ThreadId == threadId);
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
                    "Actor {ActorId} closed pending thread {ThreadId} (reason: {Reason}).",
                    Id.GetId(), threadId, reason);

                await EmitActivityEventAsync(
                    ActivityEventType.ThreadClosed,
                    $"Thread {threadId} closed ({reason ?? "no reason given"})",
                    cancellationToken,
                    details: JsonSerializer.SerializeToElement(new
                    {
                        threadId,
                        reason,
                        wasActive = false,
                    }),
                    correlationId: threadId);
                return;
            }
        }

        _logger.LogDebug(
            "Actor {ActorId} CloseConversationAsync no-op for unknown thread {ThreadId} (reason: {Reason}).",
            Id.GetId(), threadId, reason);
    }

    /// <summary>
    /// Suspends the currently active thread and moves it to the pending list.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    internal Task SuspendActiveConversationAsync(CancellationToken cancellationToken = default)
        => mailboxCoordinator.SuspendActiveConversationAsync(
            agentId: Id.GetId(),
            getActiveConversation: ct => GetActiveConversationAsync(ct),
            removeActiveConversation: ct => StateManager.TryRemoveStateAsync(StateKeys.ActiveConversation, ct),
            getPendingList: ct => GetPendingListAsync(ct),
            setPendingList: (list, ct) => StateManager.SetStateAsync(StateKeys.PendingConversations, list, ct),
            cancelActiveWork: async () =>
            {
                if (_activeWorkCancellation is not null)
                {
                    await _activeWorkCancellation.CancelAsync();
                    _activeWorkCancellation.Dispose();
                    _activeWorkCancellation = null;
                }
            },
            emitActivity: EmitActivityEventAsync,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Promotes the next pending thread to active status.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A task representing the asynchronous operation.</returns>
    internal Task PromoteNextPendingAsync(CancellationToken cancellationToken = default)
        => mailboxCoordinator.PromoteNextPendingAsync(
            agentId: Id.GetId(),
            getPendingList: ct => GetPendingListAsync(ct),
            setPendingList: (list, ct) => StateManager.SetStateAsync(StateKeys.PendingConversations, list, ct),
            removePendingList: ct => StateManager.TryRemoveStateAsync(StateKeys.PendingConversations, ct),
            setActiveConversation: (ch, ct) => StateManager.SetStateAsync(StateKeys.ActiveConversation, ch, ct),
            activateAndDispatch: async (ch, eff, ct) =>
            {
                _activeWorkCancellation = new CancellationTokenSource();
                var context = await BuildPromptAssemblyContextAsync(ch, eff, ct);
                var head = ch.Messages[0];
                PendingDispatchTask = RunDispatchAsync(head, context, _activeWorkCancellation.Token);
            },
            resolveEffectiveMetadata: (msg, ct) => ResolveEffectiveMetadataAsync(msg, ct),
            cancellationToken: cancellationToken);

    /// <summary>
    /// Enqueues a message for a pending thread, creating the channel if it does not exist.
    /// </summary>
    private Task EnqueuePendingMessageAsync(string threadId, Message message, CancellationToken cancellationToken)
        => mailboxCoordinator.EnqueuePendingMessageAsync(
            agentId: Id.GetId(),
            threadId: threadId,
            message: message,
            getPendingList: ct => GetPendingListAsync(ct),
            setPendingList: (list, ct) => StateManager.SetStateAsync(StateKeys.PendingConversations, list, ct),
            cancellationToken: cancellationToken);

    /// <summary>
    /// Gets the current status of the agent based on its state.
    /// </summary>
    private async Task<AgentStatus> GetCurrentStatusAsync(CancellationToken cancellationToken)
    {
        var activeConversation = await StateManager
            .TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, cancellationToken)
            ;

        if (!activeConversation.HasValue)
        {
            return AgentStatus.Idle;
        }

        return AgentStatus.Active;
    }

    /// <summary>
    /// Reads the current active <see cref="ThreadChannel"/> from actor state.
    /// Returns <c>null</c> when no thread is active. Used as a delegate by
    /// <see cref="IAgentMailboxCoordinator"/> calls.
    /// </summary>
    private async Task<ThreadChannel?> GetActiveConversationAsync(CancellationToken cancellationToken)
    {
        var result = await StateManager
            .TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, cancellationToken);
        return result.HasValue ? result.Value : null;
    }

    /// <summary>
    /// Reads the pending <see cref="ThreadChannel"/> list from actor state.
    /// Returns <c>null</c> when no pending list is stored. Used as a delegate
    /// by <see cref="IAgentMailboxCoordinator"/> calls.
    /// </summary>
    private async Task<List<ThreadChannel>?> GetPendingListAsync(CancellationToken cancellationToken)
    {
        var result = await StateManager
            .TryGetStateAsync<List<ThreadChannel>>(StateKeys.PendingConversations, cancellationToken);
        return result.HasValue ? result.Value : null;
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
    /// Emits a pre-built <see cref="ActivityEvent"/> through the activity event bus.
    /// Failures are logged but never allowed to escape the actor turn.
    /// Satisfies the <c>Func&lt;ActivityEvent, CancellationToken, Task&gt;</c>
    /// delegate shape expected by coordinator seams.
    /// </summary>
    private async Task EmitActivityEventAsync(ActivityEvent activityEvent, CancellationToken cancellationToken)
    {
        try
        {
            await activityEventBus.PublishAsync(activityEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit activity event {EventType} for actor {ActorId}.",
                activityEvent.EventType, Id.GetId());
        }
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
    public Task<AgentMetadata> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        return stateCoordinator.GetMetadataAsync(
            Id.GetId(),
            async ct =>
            {
                var v = await StateManager.TryGetStateAsync<string>(StateKeys.AgentModel, ct);
                return (v.HasValue, v.HasValue ? v.Value : null);
            },
            async ct =>
            {
                var v = await StateManager.TryGetStateAsync<string>(StateKeys.AgentSpecialty, ct);
                return (v.HasValue, v.HasValue ? v.Value : null);
            },
            async ct =>
            {
                var v = await StateManager.TryGetStateAsync<bool>(StateKeys.AgentEnabled, ct);
                return (v.HasValue, v.HasValue ? v.Value : default);
            },
            async ct =>
            {
                var v = await StateManager.TryGetStateAsync<AgentExecutionMode>(StateKeys.AgentExecutionMode, ct);
                return (v.HasValue, v.HasValue ? v.Value : default);
            },
            async ct =>
            {
                var v = await StateManager.TryGetStateAsync<string>(StateKeys.AgentParentUnit, ct);
                return (v.HasValue, v.HasValue ? v.Value : null);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task SetMetadataAsync(AgentMetadata metadata, CancellationToken cancellationToken = default)
    {
        return stateCoordinator.SetMetadataAsync(
            Id.GetId(),
            metadata,
            (v, ct) => StateManager.SetStateAsync(StateKeys.AgentModel, v, ct),
            (v, ct) => StateManager.SetStateAsync(StateKeys.AgentSpecialty, v, ct),
            (v, ct) => StateManager.SetStateAsync(StateKeys.AgentEnabled, v, ct),
            (v, ct) => StateManager.SetStateAsync(StateKeys.AgentExecutionMode, v, ct),
            (v, ct) => StateManager.SetStateAsync(StateKeys.AgentParentUnit, v, ct),
            EmitActivityEventAsync,
            cancellationToken);
    }

    /// <summary>
    /// Clears the agent's parent-unit pointer. Used by the unit's unassign
    /// endpoint alongside removal from the unit's <c>members</c> list, so
    /// <see cref="AgentMetadata.ParentUnit"/> and the unit's member list stay
    /// in sync. Separated from <see cref="SetMetadataAsync"/> because the
    /// partial-patch semantics there treat <c>null</c> as "leave untouched."
    /// </summary>
    public Task ClearParentUnitAsync(CancellationToken cancellationToken = default)
    {
        return stateCoordinator.ClearParentUnitAsync(
            Id.GetId(),
            ct => StateManager.RemoveStateAsync(StateKeys.AgentParentUnit, ct),
            EmitActivityEventAsync,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<string[]> GetSkillsAsync(CancellationToken cancellationToken = default)
    {
        return stateCoordinator.GetSkillsAsync(
            Id.GetId(),
            async ct =>
            {
                var v = await StateManager.TryGetStateAsync<List<string>>(StateKeys.AgentSkills, ct);
                return (v.HasValue, v.HasValue ? v.Value : null);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task SetSkillsAsync(string[] skills, CancellationToken cancellationToken = default)
    {
        return stateCoordinator.SetSkillsAsync(
            Id.GetId(),
            skills,
            (normalised, ct) => StateManager.SetStateAsync(StateKeys.AgentSkills, normalised, ct),
            EmitActivityEventAsync,
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<ExpertiseDomain[]> GetExpertiseAsync(CancellationToken cancellationToken = default)
    {
        return stateCoordinator.GetExpertiseAsync(
            Id.GetId(),
            async ct =>
            {
                var v = await StateManager.TryGetStateAsync<List<ExpertiseDomain>>(StateKeys.AgentExpertise, ct);
                return (v.HasValue, v.HasValue ? v.Value : null);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task SetExpertiseAsync(ExpertiseDomain[] domains, CancellationToken cancellationToken = default)
    {
        return stateCoordinator.SetExpertiseAsync(
            Id.GetId(),
            domains,
            (normalised, ct) => StateManager.SetStateAsync(StateKeys.AgentExpertise, normalised, ct),
            EmitActivityEventAsync,
            cancellationToken);
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

    /// <summary>
    /// Records an observation for this agent. Observations are appended to a bounded
    /// in-state channel and are drained on the next initiative reminder tick.
    /// Emits an <see cref="ActivityEventType.InitiativeTriggered"/> activity event
    /// so observers can see that the agent was poked, even when Tier 1 ignores it.
    /// </summary>
    /// <param name="observation">The observation payload.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    public Task RecordObservationAsync(JsonElement observation, CancellationToken ct)
    {
        return observationCoordinator.RecordObservationAsync(
            agentId: Id.GetId(),
            agentAddress: Address,
            observation: observation,
            getObservations: async cancellationToken =>
            {
                var existing = await StateManager
                    .TryGetStateAsync<List<JsonElement>>(StateKeys.ObservationChannel, cancellationToken);
                return existing.HasValue ? existing.Value : new List<JsonElement>();
            },
            setObservations: (list, cancellationToken) =>
                StateManager.SetStateAsync(StateKeys.ObservationChannel, list, cancellationToken),
            registerReminder: RegisterInitiativeReminderAsync,
            emitActivity: (activityEvent, cancellationToken) =>
                activityEventBus.PublishAsync(activityEvent, cancellationToken),
            cancellationToken: ct);
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
                await observationCoordinator.RunInitiativeCheckAsync(
                    agentId: Id.GetId(),
                    agentAddress: Address,
                    getObservations: async cancellationToken =>
                    {
                        var existing = await StateManager
                            .TryGetStateAsync<List<JsonElement>>(StateKeys.ObservationChannel, cancellationToken);
                        return existing.HasValue ? existing.Value : null;
                    },
                    setObservations: (list, cancellationToken) =>
                        StateManager.SetStateAsync(StateKeys.ObservationChannel, list, cancellationToken),
                    evaluateSkillPolicy: (actionType, cancellationToken) =>
                        unitPolicyEnforcer.EvaluateSkillInvocationAsync(Id.GetId(), actionType, cancellationToken),
                    evaluateInitiative: (context, cancellationToken) =>
                        initiativeEvaluator.EvaluateAsync(context, cancellationToken),
                    emitActivity: (activityEvent, cancellationToken) =>
                        activityEventBus.PublishAsync(activityEvent, cancellationToken),
                    cancellationToken: CancellationToken.None);
                break;
            default:
                _logger.LogDebug("Actor {ActorId} ignored unknown reminder {ReminderName}",
                    Id.GetId(), reminderName);
                break;
        }
    }

    /// <summary>
    /// Lazily registers the Dapr reminder that drives periodic initiative checks.
    /// The registration is idempotent — the persisted
    /// <see cref="StateKeys.InitiativeReminderRegistered"/> flag prevents duplicate work.
    /// The reminder period defaults to 12 minutes (5 calls/hour) when no policy is
    /// configured. The coordinator owns the frequency calculation; this method simply
    /// wraps the Dapr <see cref="RegisterReminderAsync"/> call so the coordinator
    /// can remain free of Dapr actor dependencies.
    /// </summary>
    private async Task RegisterInitiativeReminderAsync(CancellationToken ct)
    {
        var registered = await StateManager
            .TryGetStateAsync<bool>(StateKeys.InitiativeReminderRegistered, ct);

        if (registered.HasValue && registered.Value)
        {
            return;
        }

        // Default reminder period: 5 calls per hour = one call every 12 minutes.
        var period = TimeSpan.FromMinutes(12);

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

}