// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using System.Collections.Generic;
using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Skills;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Dapr virtual actor representing an agent in the Spring Voyage platform.
/// Implements a per-thread mailbox (#2076 / ADR-0030 §3 §44): every distinct
/// thread the agent participates in carries its own
/// <see cref="ThreadChannel"/> with a per-thread FIFO message queue and a
/// dispatching flag; concurrent threads on the same agent run
/// independently when <c>concurrent_threads</c> is <c>true</c> (the
/// default), or are serialised through an agent-wide
/// <see cref="SemaphoreSlim"/> when <c>concurrent_threads</c> is
/// <c>false</c> (mirroring the SDK runtime). The actor never performs
/// long-running work in the actor turn; it dispatches async work
/// externally per channel.
/// </summary>
public class AgentActor(
    ActorHost host,
    IActivityEventBus activityEventBus,
    IAgentObservationCoordinator observationCoordinator,
    IAgentMailboxCoordinator mailboxCoordinator,
    IAgentDispatchCoordinator dispatchCoordinator,
    IAgentDefinitionProvider agentDefinitionProvider,
    IUnitMembershipRepository membershipRepository,
    IUnitPolicyEnforcer unitPolicyEnforcer,
    IAgentInitiativeEvaluator initiativeEvaluator,
    ILoggerFactory loggerFactory,
    IAgentLifecycleCoordinator lifecycleCoordinator,
    IAgentStateCoordinator stateCoordinator,
    IAgentAmendmentCoordinator amendmentCoordinator,
    IAgentUnitPolicyCoordinator unitPolicyCoordinator,
    IExpertiseSeedProvider? expertiseSeedProvider = null,
    IActorProxyFactory? actorProxyFactory = null,
    IDirectoryService? directoryService = null,
    IRuntimeInvocationPath? runtimeInvocationPath = null,
    IServiceScopeFactory? scopeFactory = null,
    Cvoya.Spring.Core.Issues.IIssueWriter? issueWriter = null,
    IArtefactValidationCoordinator? validationCoordinator = null) : Actor(host), IAgentActor, IRemindable
{
    /// <summary>
    /// Name of the Dapr reminder that drives periodic initiative checks.
    /// </summary>
    internal const string InitiativeReminderName = "initiative-check";

    private readonly ILogger _logger = loggerFactory.CreateLogger<AgentActor>();

    /// <summary>
    /// Per-thread dispatcher tracker — one entry per thread the agent is
    /// currently dispatching, carrying that thread's
    /// <see cref="CancellationTokenSource"/>. Cancel-message handling
    /// cancels only the requested thread's source; other threads continue
    /// independently (ADR-0030 §44). Shared with <see cref="UnitActor"/>
    /// (#2491) so both subjects on the
    /// <see cref="IRuntimeInvocationPath"/> seam expose the same in-flight
    /// bookkeeping.
    /// </summary>
    private readonly ActorDispatchChannelTracker _activeWorkByThread = new();

    /// <summary>
    /// Agent-wide serialisation lock used when the agent's
    /// <c>concurrent_threads</c> policy is <c>false</c>. Lazily created on
    /// the first dispatch when <see cref="GetConcurrentThreadsAsync"/>
    /// reports <c>false</c>; remains <c>null</c> when the default
    /// (<c>true</c>) is in effect. Mirrors the SDK runtime's
    /// <c>asyncio.Lock</c> pattern (agents/spring-voyage-agent-sdk/spring_voyage_agent_sdk/runtime.py).
    /// </summary>
    private SemaphoreSlim? _agentWideLock;

    /// <summary>
    /// Cached <c>concurrent_threads</c> value resolved from
    /// <see cref="IAgentDefinitionProvider"/> on the first dispatch. Per
    /// ADR-0030 §3 the flag is part of the agent / unit definition; it
    /// does not change during a live actor's lifetime, so a single read
    /// is sufficient. <c>null</c> means "not yet resolved" — the next
    /// dispatch path resolves it.
    /// </summary>
    private bool? _concurrentThreads;

    /// <summary>
    /// Exposed for tests: the currently running dispatch task (if any).
    /// Production callers should not depend on this field. Under
    /// concurrent threads the agent may have multiple dispatchers in
    /// flight simultaneously; this field tracks only the most-recently
    /// launched one.
    /// </summary>
    internal Task? PendingDispatchTask { get; private set; }

    /// <summary>
    /// Gets the address of this agent actor.
    /// </summary>
    public Address Address => Address.For("agent", Id.GetId());

    /// <summary>
    /// Runs the actor-activation logic by delegating to
    /// <see cref="IAgentLifecycleCoordinator"/>. The coordinator handles
    /// expertise seeding from <c>AgentDefinition</c> YAML (#488).
    /// </summary>
    protected override async Task OnActivateAsync()
    {
        await lifecycleCoordinator.ActivateAsync(
            Id.GetId(),
            ct => stateCoordinator.HasExpertiseSetAsync(Id.GetId(), ct),
            ct => expertiseSeedProvider is not null
                ? expertiseSeedProvider.GetAgentSeedAsync(Id.GetId(), ct)
                : Task.FromResult<IReadOnlyList<ExpertiseDomain>?>(null),
            (domains, ct) => SetExpertiseAsync(domains, ct),
            CancellationToken.None);

        await base.OnActivateAsync();
    }

    /// <summary>
    /// #2160: producer-side seam for the operational-issues surface.
    /// Optional so test harnesses that construct the actor with a
    /// partial dependency set still wire up.
    /// </summary>
    private readonly Cvoya.Spring.Core.Issues.IIssueWriter? _issueWriter = issueWriter;

    /// <inheritdoc />
    public async Task<Message?> ReceiveAsync(Message message, CancellationToken cancellationToken = default)
    {
        Exception? caughtException = null;
        try
        {
            await EmitActivityEventAsync(ActivityEventType.MessageArrived,
                MessageArrivedDetails.BuildSummary(message),
                cancellationToken,
                details: MessageArrivedDetails.Build(message),
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
            caughtException = ex;
            _logger.LogError(ex, "Unhandled exception processing message {MessageId} of type {MessageType} in actor {ActorId}",
                message.Id, message.Type, Id.GetId());

            await EmitActivityEventAsync(ActivityEventType.ErrorOccurred,
                $"Error processing message {message.Id}: {ex.Message}",
                cancellationToken,
                details: JsonSerializer.SerializeToElement(new
                {
                    error = ex.Message,
                    agentId = Id.GetId(),
                    threadId = message.ThreadId,
                }),
                correlationId: message.ThreadId);

            return CreateErrorResponse(message, ex.Message);
        }
        catch (SpringException ex)
        {
            // SpringExceptions carry a structured "<Code>: <message>" form by
            // convention (CredentialFormatRejected, ImagePullFailed, …). We
            // re-throw — callers depend on the exception bubbling — but
            // record an Issue so the Overview surfaces the condition without
            // waiting for the next validation run.
            caughtException = ex;
            throw;
        }
        finally
        {
            // #2160: bridge runtime errors to the Issues surface. A
            // successful pass clears prior runtime-source issues; an
            // exception opens one keyed on the structured code if we can
            // extract one, else a generic AgentRuntimeError.
            if (_issueWriter is not null)
            {
                await TryPublishRuntimeIssueAsync(caughtException, cancellationToken);
            }
        }
    }

    /// <summary>
    /// #2160: bridge agent message handling to the Issues surface.
    /// Best-effort — never let issue-publish failures derail the
    /// agent's own response path.
    /// </summary>
    /// <summary>
    /// #2160 / #2189: bridge agent message handling to the Issues
    /// surface. On a clean pass clears every prior open issue against
    /// this agent across the producer-side source buckets — so a
    /// recovered runtime / credential / configuration condition stops
    /// painting the Overview red. On failure publishes one tagged
    /// issue keyed on the source bucket the producer stamped (or
    /// <c>"runtime"</c> when no tag is present).
    /// </summary>
    private async Task TryPublishRuntimeIssueAsync(
        Exception? caught, CancellationToken cancellationToken)
    {
        try
        {
            var subjectId = await ResolveOwnAgentIdAsync(cancellationToken);
            if (subjectId is null)
            {
                return;
            }
            var subject = new Cvoya.Spring.Core.Issues.IssueSubject(
                Cvoya.Spring.Core.Issues.IssueSubjectKind.Agent,
                subjectId.Value);

            if (caught is null)
            {
                // Clear every producer-source bucket that PR #2189
                // attributes to the agent dispatch path. Iterating the
                // closed set keeps the on-clear path symmetric with the
                // on-fail path (which writes one tagged issue per
                // bucket). New buckets must extend this set so a
                // recovery actually paints them green.
                foreach (var source in AgentRuntimeIssueSources)
                {
                    await _issueWriter!.ClearAsync(subject, source: source, code: null, cancellationToken);
                }
                return;
            }

            var classification = ClassifyAgentRuntimeException(caught);
            await _issueWriter!.UpsertAsync(
                subject,
                Cvoya.Spring.Core.Issues.IssueSeverity.Error,
                source: classification.Source,
                code: classification.Code,
                title: classification.Title,
                detail: null,
                traceId: null,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish runtime issue for agent {AgentId}.",
                Id.GetId());
        }
    }

    /// <summary>
    /// Closed set of source buckets producers attribute issues to via
    /// <see cref="SpringException.WithIssue"/>. Mirrored on-clear so a
    /// recovered condition drops every prior open row regardless of
    /// which bucket the producer originally tagged it under.
    /// </summary>
    private static readonly string[] AgentRuntimeIssueSources =
        ["runtime", "credential", "configuration"];

    /// <summary>
    /// Resolve the operationally-significant <c>(source, code, title)</c>
    /// triple for a thrown exception.
    /// <list type="number">
    ///   <item>
    ///     #2189: prefer the producer-stamped tags on
    ///     <see cref="Exception.Data"/> (<see cref="SpringException.IssueCodeDataKey"/>
    ///     / <see cref="SpringException.IssueSourceDataKey"/>) — these
    ///     are precise and don't depend on message shape.
    ///   </item>
    ///   <item>
    ///     Fall back to the legacy <c>"&lt;Code&gt;: &lt;message&gt;"</c>
    ///     prefix heuristic so producers that haven't been migrated yet
    ///     still surface a stable code (always under
    ///     <c>source="runtime"</c>).
    ///   </item>
    ///   <item>
    ///     Final fallback: <c>("runtime", "AgentRuntimeError",
    ///     ex.Message ?? "Agent runtime failed.")</c>.
    ///   </item>
    /// </list>
    /// </summary>
    internal static AgentRuntimeIssueClassification ClassifyAgentRuntimeException(Exception ex)
    {
        var message = ex.Message ?? string.Empty;

        // Producer-stamped tags win. Read both keys from ex.Data and
        // require both to be present + well-shaped — a half-tagged
        // exception would otherwise mix a precise source with a
        // heuristic code (or vice versa) and confuse the surface.
        var taggedCode = ex.Data[SpringException.IssueCodeDataKey] as string;
        var taggedSource = ex.Data[SpringException.IssueSourceDataKey] as string;
        if (!string.IsNullOrWhiteSpace(taggedCode) && !string.IsNullOrWhiteSpace(taggedSource))
        {
            // Title: prefer the post-prefix slice when the message also
            // carries the convention (so the Overview reads cleanly);
            // otherwise the full message.
            var title = ExtractMessageTitle(message, taggedCode);
            return new AgentRuntimeIssueClassification(
                Source: taggedSource!,
                Code: taggedCode!,
                Title: title);
        }

        // Legacy prefix heuristic — kept so producers that haven't been
        // migrated still get a stable code under source="runtime".
        var colon = message.IndexOf(':');
        if (colon > 0 && colon < 64)
        {
            var prefix = message[..colon].Trim();
            if (prefix.Length > 0
                && char.IsUpper(prefix[0])
                && prefix.All(c => char.IsLetterOrDigit(c)))
            {
                return new AgentRuntimeIssueClassification(
                    Source: "runtime",
                    Code: prefix,
                    Title: message[(colon + 1)..].Trim() is { Length: > 0 } detail
                        ? detail
                        : prefix);
            }
        }

        return new AgentRuntimeIssueClassification(
            Source: "runtime",
            Code: "AgentRuntimeError",
            Title: message.Length > 0 ? message : "Agent runtime failed.");
    }

    private static string ExtractMessageTitle(string message, string taggedCode)
    {
        if (message.Length == 0) return taggedCode;
        var colon = message.IndexOf(':');
        if (colon > 0 && colon < 64
            && string.Equals(message[..colon].Trim(), taggedCode, StringComparison.Ordinal))
        {
            var rest = message[(colon + 1)..].Trim();
            return rest.Length > 0 ? rest : taggedCode;
        }
        return message;
    }

    private async Task<Guid?> ResolveOwnAgentIdAsync(CancellationToken cancellationToken)
    {
        if (directoryService is null)
        {
            return null;
        }
        var address = Address.For("agent", Id.GetId());
        var entry = await directoryService.ResolveAsync(address, cancellationToken);
        return entry?.ActorId;
    }

    /// <summary>
    /// Cancels the dispatcher for the supplied thread (per ADR-0030 §44 the
    /// cancel is per-thread; other threads on the same agent are
    /// unaffected). Removes that thread's channel and channel-index entry
    /// so a subsequent inbound on the same thread starts a fresh drain
    /// loop. Messages targeting other threads on this agent continue
    /// running in their own dispatchers.
    /// </summary>
    private async Task<Message?> HandleCancelAsync(Message message, CancellationToken cancellationToken)
    {
        var threadId = message.ThreadId;
        _logger.LogInformation("Actor {ActorId} received cancel for thread {ThreadId}",
            Id.GetId(), threadId);

        if (string.IsNullOrEmpty(threadId))
        {
            // Cancel without a thread id is a no-op — it would have been
            // ambiguous under the per-thread model anyway. We still return
            // the ack so callers don't see a 5xx.
            return CreateAckResponse(message);
        }

        // Cancel any dispatcher running for this thread. Other threads'
        // dispatchers are untouched.
        await _activeWorkByThread.CancelAsync(threadId);

        var channel = await GetChannelAsync(threadId, cancellationToken);
        if (channel is not null)
        {
            await RemoveChannelAsync(threadId, cancellationToken);
            _logger.LogInformation(
                "Actor {ActorId} cleared channel for cancelled thread {ThreadId}",
                Id.GetId(), threadId);
        }

        return CreateAckResponse(message);
    }

    /// <summary>
    /// Returns a status payload that lists every per-thread channel and
    /// its current queue depth (#2076 / ADR-0030 §3 §44). Under concurrent
    /// threads a single binary "Active" flag is no longer well-defined —
    /// the agent may be running on N threads simultaneously, each at its
    /// own depth. The <c>Status</c> string is kept for the
    /// <c>sv.directory.get_status</c> tool's child-status mapping
    /// ("Idle" or "Active"), but the real shape is
    /// the per-thread <c>ThreadDepths</c> map.
    /// </summary>
    private async Task<Message?> HandleStatusQueryAsync(Message message, CancellationToken cancellationToken)
    {
        var threadIds = await GetChannelIndexAsync(cancellationToken);
        var depths = new Dictionary<string, int>(threadIds.Count);
        foreach (var tid in threadIds)
        {
            var channel = await GetChannelAsync(tid, cancellationToken);
            if (channel is not null)
            {
                depths[tid] = channel.Messages.Count;
            }
        }

        var status = depths.Count == 0 ? AgentStatus.Idle : AgentStatus.Active;

        var statusPayload = JsonSerializer.SerializeToElement(new
        {
            Status = status.ToString(),
            ThreadDepths = depths,
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
    /// Handles a mid-flight amendment message (#142) by delegating to
    /// <see cref="IAgentAmendmentCoordinator"/>.
    /// </summary>
    private async Task<Message?> HandleAmendmentAsync(Message message, CancellationToken cancellationToken)
    {
        await amendmentCoordinator.HandleAmendmentAsync(
            agentId: Id.GetId(),
            message: message,
            getMembership: async (unitSlug, ct) =>
            {
                if (directoryService is null)
                {
                    return null;
                }

                var unitEntry = await directoryService.ResolveAsync(Address.For("unit", unitSlug), ct);
                if (unitEntry is null)
                {
                    return null;
                }
                var unitUuid = unitEntry.ActorId;

                if (!Guid.TryParse(Id.GetId(), out var agentUuid))
                {
                    return null;
                }

                return await membershipRepository.GetAsync(unitId: unitUuid, agentId: agentUuid, ct);
            },
            getPendingAmendments: async ct =>
            {
                var v = await StateManager
                    .TryGetStateAsync<List<PendingAmendment>>(StateKeys.AgentPendingAmendments, ct);
                return (v.HasValue, v.HasValue ? v.Value : null);
            },
            setPendingAmendments: (list, ct) =>
                StateManager.SetStateAsync(StateKeys.AgentPendingAmendments, list, ct),
            cancelActiveWork: async () =>
            {
                // StopAndWait amendments cancel any dispatcher running on
                // the amendment's thread. Per ADR-0030 §44 the cancel is
                // per-thread — other threads are unaffected. The
                // amendment payload's CorrelationId is the thread id.
                var ct = TryReadAmendmentCorrelationId(message);
                if (ct is null)
                {
                    return;
                }
                await _activeWorkByThread.CancelAsync(ct);
            },
            setPaused: (ct) => StateManager.SetStateAsync(StateKeys.AgentPaused, true, ct),
            emitActivity: EmitActivityEventAsync,
            cancellationToken: cancellationToken);

        return CreateAckResponse(message);
    }

    private static string? TryReadAmendmentCorrelationId(Message message)
    {
        try
        {
            if (message.Payload.ValueKind == JsonValueKind.Object &&
                message.Payload.TryGetProperty("CorrelationId", out var corr) &&
                corr.ValueKind == JsonValueKind.String)
            {
                return corr.GetString();
            }
        }
        catch
        {
            // Best-effort — fall through to message.ThreadId.
        }
        return message.ThreadId;
    }

    /// <summary>
    /// Clears the <see cref="StateKeys.AgentPaused"/> flag.
    /// </summary>
    internal Task ResumeFromPauseAsync(CancellationToken cancellationToken = default)
        => StateManager.TryRemoveStateAsync(StateKeys.AgentPaused, cancellationToken);

    /// <summary>
    /// Routes a domain message to its per-thread channel, creating the
    /// channel and launching a dispatcher when none exists for the
    /// thread (#2076 / ADR-0030 §3 §44). Concurrent threads on the same
    /// agent run independently; per-thread FIFO is preserved within each
    /// channel.
    /// </summary>
    private async Task<Message?> HandleDomainMessageAsync(Message message, CancellationToken cancellationToken)
    {
        var threadId = message.ThreadId
            ?? throw new CallerValidationException(
                CallerValidationCodes.MissingThreadId,
                "Domain messages must have a ThreadId");

        var effective = await ResolveEffectiveMetadataAsync(message, cancellationToken);

        await mailboxCoordinator.HandleDomainMessageAsync(
            agentId: Id.GetId(),
            message: message,
            effective: effective,
            applyUnitPolicies: (eff, ct) => ApplyUnitPoliciesAsync(eff, ct),
            getChannel: (tid, ct) => GetChannelAsync(tid, ct),
            saveChannel: (ch, ct) => SaveChannelAsync(ch, ct),
            dispatch: async (ch, eff, ct) =>
            {
                var cts = _activeWorkByThread.Enter(ch.ThreadId);
                var context = await BuildPromptAssemblyContextAsync(ch, eff, ct);
                PendingDispatchTask = DispatchAsync(ch.Messages[0], context, cts.Token);
            },
            emitActivity: EmitActivityEventAsync,
            cancellationToken: cancellationToken);

        return CreateAckResponse(message);
    }

    /// <summary>
    /// Builds the prompt-assembly context for a per-thread channel.
    /// </summary>
    private async Task<PromptAssemblyContext> BuildPromptAssemblyContextAsync(
        ThreadChannel channel,
        AgentMetadata effective,
        CancellationToken cancellationToken)
    {
        var definition = await agentDefinitionProvider.GetByIdAsync(Id.GetId(), cancellationToken);

        var pendingAmendments = await StateManager
            .TryGetStateAsync<List<PendingAmendment>>(StateKeys.AgentPendingAmendments, cancellationToken);

        IReadOnlyList<PendingAmendment>? amendments = pendingAmendments.HasValue && pendingAmendments.Value.Count > 0
            ? pendingAmendments.Value
            : null;

        // Skill-bundle equipage (#2360 + #2363). The agent store feeds
        // Layer 4 keyed by this actor's id. The unit store feeds Layer 2
        // and is read twice — once keyed by the actor's own id (the
        // unit-as-agent case from ADR-0039) and once per parent unit
        // resolved from IUnitMembershipRepository (the leaf-agent → unit
        // inheritance hop). Helper dedups on (package, skill) and orders
        // multi-parent contributions alphabetically by parent display
        // name. Both stores are scoped DI services so the helper opens
        // its own scope; v0.1 keeps inheritance one hop deep — sub-unit
        // nesting is out of scope and not consulted.
        var (unitBundles, agentBundles) = await LoadEquippedBundlesAsync(cancellationToken);

        // The always-on platform-tool catalog rides Layer 1
        // (IPlatformPromptProvider) since #2670, so no per-actor skill-
        // registry projection is required here.
        return new PromptAssemblyContext(
            Policies: null,
            AgentInstructions: definition?.Instructions,
            EffectiveMetadata: effective,
            SkillBundles: unitBundles,
            AgentSkillBundles: agentBundles,
            PendingAmendments: amendments);
    }

    /// <summary>
    /// Resolves the unit-scoped (Layer 2) and agent-scoped (Layer 4)
    /// skill bundles for this actor. Delegates to
    /// <see cref="EquippedBundleLoader"/> so the inheritance logic — the
    /// leaf-agent → parent-unit Layer-2 hop wired in #2363 — is unit-
    /// testable in isolation. When the scope factory is unavailable
    /// (legacy test compositions without DI) the call returns
    /// <c>(null, null)</c> so the prompt-assembly path degrades to the
    /// no-bundle render.
    /// </summary>
    private Task<(IReadOnlyList<SkillBundle>? Unit, IReadOnlyList<SkillBundle>? Agent)> LoadEquippedBundlesAsync(
        CancellationToken cancellationToken) =>
        EquippedBundleLoader.LoadAsync(scopeFactory, Id.GetId(), cancellationToken);

    /// <summary>
    /// Resolves the effective per-turn metadata for <paramref name="message"/>.
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
            if (directoryService is null)
            {
                return global;
            }

            var unitEntry = await directoryService.ResolveAsync(message.From, cancellationToken);
            if (unitEntry is null
                || !Guid.TryParse(Id.GetId(), out var agentUuid))
            {
                return global;
            }

            membership = await membershipRepository.GetAsync(
                unitId: unitEntry.ActorId,
                agentId: agentUuid,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Membership lookup failed for agent {ActorId} and unit {UnitId}; using agent-global metadata.",
                Id.GetId(), message.From.Path);
            return global;
        }

        if (membership is null)
        {
            return global;
        }

        return new AgentMetadata(
            Model: membership.Model ?? global.Model,
            Specialty: membership.Specialty ?? global.Specialty,
            Enabled: membership.Enabled,
            ExecutionMode: membership.ExecutionMode ?? global.ExecutionMode,
            ParentUnit: global.ParentUnit);
    }

    /// <summary>
    /// Applies unit-level policy dimensions (#247 model, #248 cost, #249
    /// execution mode) by delegating to <see cref="IAgentUnitPolicyCoordinator"/>.
    /// </summary>
    private Task<(AgentMetadata Effective, PolicyVerdict? Verdict)> ApplyUnitPoliciesAsync(
        AgentMetadata effective, CancellationToken cancellationToken)
    {
        var agentId = Id.GetId();
        return unitPolicyCoordinator.ApplyUnitPoliciesAsync(
            agentId: agentId,
            effective: effective,
            evaluateModel: (id, model, ct) =>
                unitPolicyEnforcer.EvaluateModelAsync(id, model, ct),
            evaluateCost: (id, cost, ct) =>
                unitPolicyEnforcer.EvaluateCostAsync(id, cost, ct),
            resolveExecutionMode: (id, mode, ct) =>
                unitPolicyEnforcer.ResolveExecutionModeAsync(id, mode, ct),
            cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Dispatches a single message for a per-thread channel. When the
    /// agent's <c>concurrent_threads</c> policy is <c>false</c>, the
    /// dispatch acquires the agent-wide lock first so concurrent threads
    /// run serialised at the agent level (mirroring the SDK runtime's
    /// asyncio.Lock pattern). The lock release happens in
    /// <c>finally</c> so a cancelled or failing dispatch does not pin
    /// the agent.
    /// </summary>
    private async Task DispatchAsync(Message message, PromptAssemblyContext context, CancellationToken ct)
    {
        var threadId = message.ThreadId ?? string.Empty;
        var concurrent = await GetConcurrentThreadsAsync(ct);
        SemaphoreSlim? gate = concurrent ? null : GetOrCreateAgentWideLock();

        try
        {
            if (gate is not null)
            {
                await gate.WaitAsync(ct);
            }
            try
            {
                if (runtimeInvocationPath is not null)
                {
                    await runtimeInvocationPath.InvokeAsync(
                        Address,
                        message,
                        context,
                        EmitActivityEventAsync,
                        reason => SignalDispatchExitViaSelfAsync(threadId, reason),
                        ct);
                    return;
                }

                await dispatchCoordinator.RunDispatchAsync(
                    Id.GetId(),
                    message,
                    context,
                    EmitActivityEventAsync,
                    reason => SignalDispatchExitViaSelfAsync(threadId, reason),
                    ct);
            }
            finally
            {
                gate?.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // The gate.WaitAsync may throw on cancel before the
            // dispatcher gets a chance to run. Make sure the per-thread
            // exit still runs so the channel doesn't sit in a stuck
            // Dispatching state. Other catch-paths are owned by the
            // dispatch coordinator and already invoke onDispatchExit.
            await SignalDispatchExitViaSelfAsync(threadId, "dispatch cancelled before run");
        }
    }

    private SemaphoreSlim GetOrCreateAgentWideLock()
    {
        // First-write-wins; the actor turn model means there is at most
        // one concurrent caller into HandleDomainMessageAsync, so this
        // does not race in practice.
        return _agentWideLock ??= new SemaphoreSlim(1, 1);
    }

    /// <summary>
    /// Resolves the agent's <c>concurrent_threads</c> policy from the
    /// definition provider, caching the result for the actor's lifetime
    /// (the flag is not editable at runtime). Defaults to <c>true</c>
    /// when no definition is available — concurrent threads is the
    /// platform default per ADR-0030 §3.
    /// </summary>
    private async Task<bool> GetConcurrentThreadsAsync(CancellationToken cancellationToken)
    {
        if (_concurrentThreads.HasValue)
        {
            return _concurrentThreads.Value;
        }

        try
        {
            var definition = await agentDefinitionProvider.GetByIdAsync(Id.GetId(), cancellationToken);
            // Default to true (ADR-0030 §3) when no execution config is
            // present. The flag is on AgentExecutionConfig (the YAML
            // execution: block); leaf agents may have no execution
            // config at all in older fixtures.
            _concurrentThreads = definition?.Execution?.ConcurrentThreads ?? true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to resolve concurrent_threads for actor {ActorId}; defaulting to true.",
                Id.GetId());
            _concurrentThreads = true;
        }

        return _concurrentThreads.Value;
    }

    private async Task SignalDispatchExitViaSelfAsync(string threadId, string reason)
    {
        // AgentDispatchCoordinator.RunDispatchAsync runs outside the actor
        // turn, so we can't touch StateManager directly. When an actor proxy
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
                await self.OnDispatchExitAsync(threadId, reason, CancellationToken.None);
            }
            else
            {
                await OnDispatchExitAsync(threadId, reason, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to signal dispatch exit for actor {ActorId} thread {ThreadId} (reason: {Reason}).",
                Id.GetId(), threadId, reason);
        }
    }

    /// <inheritdoc />
    public async Task OnDispatchExitAsync(
        string threadId,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(threadId))
        {
            _logger.LogWarning(
                "Actor {ActorId} OnDispatchExitAsync called without a thread id (reason: {Reason}).",
                Id.GetId(), reason);
            return;
        }

        var channel = await GetChannelAsync(threadId, cancellationToken);
        if (channel is null)
        {
            // Channel was already cleared (e.g. by a per-thread cancel).
            // Drop the per-thread CTS bookkeeping if it's still around.
            _activeWorkByThread.Exit(threadId);
            _logger.LogDebug(
                "Actor {ActorId} OnDispatchExitAsync no-op for thread {ThreadId} (reason: {Reason}).",
                Id.GetId(), threadId, reason);
            return;
        }

        // Remove the head message that was just dispatched. The dispatcher
        // ran for channel.Messages[0]; subsequent appends have queued
        // behind it. Removing the head preserves per-thread FIFO for the
        // remaining queue.
        if (channel.Messages.Count > 0)
        {
            channel.Messages.RemoveAt(0);
        }

        // Dispose the per-thread CTS — the dispatcher returned, the token
        // is no longer needed.
        _activeWorkByThread.Exit(threadId);

        if (channel.Messages.Count == 0)
        {
            // Drain complete. Remove the channel so a subsequent inbound
            // on the same thread starts fresh. Per-ADR-0030 §44 there is
            // no agent-level "Idle" StateChanged event — the agent may
            // still be Active on N other threads.
            await RemoveChannelAsync(threadId, cancellationToken);
            _logger.LogInformation(
                "Actor {ActorId} thread {ThreadId} drain complete (reason: {Reason})",
                Id.GetId(), threadId, reason);
            return;
        }

        // Drain the next queued message: re-mark dispatching, save the
        // channel with the head removed, and fire a fresh dispatcher
        // for the new head. The mailbox coordinator's Case 2 (in-flight)
        // assumes Dispatching == true while the loop is running.
        channel.Dispatching = true;
        await SaveChannelAsync(channel, cancellationToken);

        var head = channel.Messages[0];
        var effective = await ResolveEffectiveMetadataAsync(head, cancellationToken);
        var context = await BuildPromptAssemblyContextAsync(channel, effective, cancellationToken);
        var newCts = _activeWorkByThread.Enter(threadId);
        PendingDispatchTask = DispatchAsync(head, context, newCts.Token);
    }

    private async Task<ThreadChannel?> GetChannelAsync(string threadId, CancellationToken cancellationToken)
    {
        var key = StateKeys.ChannelPrefix + threadId;
        var result = await StateManager.TryGetStateAsync<ThreadChannel>(key, cancellationToken);
        return result.HasValue ? result.Value : null;
    }

    private async Task SaveChannelAsync(ThreadChannel channel, CancellationToken cancellationToken)
    {
        var key = StateKeys.ChannelPrefix + channel.ThreadId;
        await StateManager.SetStateAsync(key, channel, cancellationToken);

        var index = await GetChannelIndexAsync(cancellationToken);
        if (!index.Contains(channel.ThreadId))
        {
            index.Add(channel.ThreadId);
            await StateManager.SetStateAsync(StateKeys.ChannelIndex, index, cancellationToken);
        }
    }

    private async Task RemoveChannelAsync(string threadId, CancellationToken cancellationToken)
    {
        var key = StateKeys.ChannelPrefix + threadId;
        await StateManager.TryRemoveStateAsync(key, cancellationToken);

        var index = await GetChannelIndexAsync(cancellationToken);
        if (index.Remove(threadId))
        {
            if (index.Count == 0)
            {
                await StateManager.TryRemoveStateAsync(StateKeys.ChannelIndex, cancellationToken);
            }
            else
            {
                await StateManager.SetStateAsync(StateKeys.ChannelIndex, index, cancellationToken);
            }
        }
    }

    private async Task<List<string>> GetChannelIndexAsync(CancellationToken cancellationToken)
    {
        var result = await StateManager
            .TryGetStateAsync<List<string>>(StateKeys.ChannelIndex, cancellationToken);
        return result.HasValue ? result.Value : new List<string>();
    }

    /// <inheritdoc />
    public async Task<AgentRuntimeStatusReport> GetRuntimeStatusAsync(
        CancellationToken cancellationToken = default)
    {
        // Sum per-thread channel state. Each ThreadChannel.Dispatching
        // mirrors the dispatcher state the mailbox coordinator manages
        // (#2076); each channel's Messages.Count is the FIFO queue depth
        // including the in-flight head.
        //
        // In-flight count := number of channels with Dispatching == true.
        // Queue count := total messages across channels, minus the
        // in-flight heads (so a channel with Dispatching == true and
        // 1 message contributes 0 queued; with 3 messages it contributes
        // 2 queued). A channel with Dispatching == false but messages
        // present is a transient state between drains; we count those
        // messages as queued so the API surfaces the correct
        // "queued ahead" signal.
        var threadIds = await GetChannelIndexAsync(cancellationToken);
        var inFlight = 0;
        var queued = 0;
        var channelCount = 0;

        foreach (var tid in threadIds)
        {
            var channel = await GetChannelAsync(tid, cancellationToken);
            if (channel is null)
            {
                continue;
            }

            channelCount++;
            var depth = channel.Messages.Count;
            if (channel.Dispatching)
            {
                inFlight++;
                queued += Math.Max(0, depth - 1);
            }
            else
            {
                queued += depth;
            }
        }

        return new AgentRuntimeStatusReport(
            InFlightThreadCount: inFlight,
            QueuedMessageCount: queued,
            ChannelCount: channelCount,
            ObservedAt: DateTimeOffset.UtcNow);
    }

    // ──────────────────────────────────────────────────────────────────────
    // Lifecycle state machine (#2364) — mirrors UnitActor.
    // ──────────────────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task<LifecycleStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        => GetStatusInternalAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<string?> GetLifecycleErrorAsync(CancellationToken cancellationToken = default)
    {
        var result = await StateManager
            .TryGetStateAsync<string>(StateKeys.AgentLifecycleError, cancellationToken);
        return result.HasValue ? result.Value : null;
    }

    /// <inheritdoc />
    public async Task<TransitionResult> TransitionAsync(
        LifecycleStatus target,
        CancellationToken cancellationToken = default)
    {
        var current = await GetStatusInternalAsync(cancellationToken);

        if (!LifecycleTransitions.IsValidTransition(current, target))
        {
            var reason = $"cannot transition from {current} to {target}";
            _logger.LogWarning(
                "Agent {ActorId} rejected transition from {Current} to {Target}: {Reason}",
                Id.GetId(), current, target, reason);
            return new TransitionResult(false, current, reason);
        }

        var result = await PersistTransitionAsync(current, target, failure: null, cancellationToken);

        // #2364: on entry into Validating, schedule the shared
        // ArtefactValidationWorkflow so the agent's image+credential+model
        // get probed. Mirrors UnitActor.TransitionAsync; the coordinator
        // routes by ArtefactKind.Agent so the per-kind execution-store
        // lookup hits AgentDefinitions.
        if (result.Success && target == LifecycleStatus.Validating && validationCoordinator is not null)
        {
            var recoveryResult = await validationCoordinator.TryStartWorkflowAsync(
                ArtefactKind.Agent, Id.GetId(), PersistTransitionAsync, cancellationToken);
            if (recoveryResult is not null)
            {
                return recoveryResult;
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<TransitionResult> CompleteValidationAsync(
        ArtefactValidationCompletion completion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);

        TransitionResult result;
        if (validationCoordinator is not null)
        {
            result = await validationCoordinator.CompleteValidationAsync(
                ArtefactKind.Agent,
                Id.GetId(),
                completion,
                GetStatusInternalAsync,
                PersistTransitionAsync,
                cancellationToken);
        }
        else
        {
            // Legacy fallback (no coordinator wired — test harnesses):
            // apply the appropriate transition inline.
            var current = await GetStatusInternalAsync(cancellationToken);
            if (current == LifecycleStatus.Stopped || current == LifecycleStatus.Error)
            {
                return new TransitionResult(false, current, $"validation completion ignored: agent already {current}");
            }
            if (current != LifecycleStatus.Validating)
            {
                return new TransitionResult(false, current, $"validation completion ignored: status is {current}, expected Validating");
            }
            result = await PersistTransitionAsync(
                LifecycleStatus.Validating,
                completion.Success ? LifecycleStatus.Stopped : LifecycleStatus.Error,
                completion.Success ? null : completion.Failure,
                cancellationToken);
        }

        // #2364: auto-start the agent after a successful validation when
        // the activator marked it as pending. Mirrors UnitActor's pattern
        // but skips the connector-dispatcher hop — agents have no
        // connector bindings in v0.1.
        if (result.Success && result.CurrentStatus == LifecycleStatus.Stopped)
        {
            await TryAutoStartAsync(cancellationToken);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task SetPendingAutoStartAsync(CancellationToken cancellationToken = default)
    {
        // Idempotent — overwriting with the same value is fine; subsequent
        // SaveStateAsync persists the flag so the next CompleteValidationAsync
        // turn observes it.
        await StateManager.SetStateAsync(StateKeys.AgentPendingAutoStart, true, cancellationToken);
        await StateManager.SaveStateAsync(cancellationToken);
    }

    /// <summary>
    /// Reads the persisted lifecycle status, defaulting to
    /// <see cref="LifecycleStatus.Draft"/> when unset.
    /// </summary>
    private async Task<LifecycleStatus> GetStatusInternalAsync(CancellationToken ct)
    {
        var result = await StateManager
            .TryGetStateAsync<LifecycleStatus>(StateKeys.AgentLifecycleStatus, ct);
        return result.HasValue ? result.Value : LifecycleStatus.Draft;
    }

    /// <summary>
    /// Persists the status transition and emits a <c>StateChanged</c>
    /// activity event. Mirrors <c>UnitActor.PersistTransitionAsync</c> and
    /// also clears <see cref="StateKeys.AgentLifecycleError"/> on any
    /// non-Error transition so the row stays internally consistent.
    /// </summary>
    private async Task<TransitionResult> PersistTransitionAsync(
        LifecycleStatus current,
        LifecycleStatus target,
        ArtefactValidationError? failure,
        CancellationToken ct)
    {
        await StateManager.SetStateAsync(StateKeys.AgentLifecycleStatus, target, ct);

        if (target == LifecycleStatus.Error)
        {
            if (failure is not null && !string.IsNullOrEmpty(failure.Message))
            {
                await StateManager.SetStateAsync(StateKeys.AgentLifecycleError, failure.Message, ct);
            }
            // else: leave whatever message was there, or absent
        }
        else
        {
            await StateManager.TryRemoveStateAsync(StateKeys.AgentLifecycleError, ct);
        }

        await StateManager.SaveStateAsync(ct);

        _logger.LogInformation(
            "Agent {ActorId} transitioned from {Current} to {Target}",
            Id.GetId(), current, target);

        // Activity-event emission. AgentActor's existing activity surface
        // is owned by the dispatch coordinator; for the lifecycle path we
        // emit a focused StateChanged event via the bus directly only when
        // the bus is available — falls back to logging in test harnesses.
        try
        {
            var summary = failure is not null
                ? $"Agent transitioned from {current} to {target}: {failure.Code} — {failure.Message}"
                : $"Agent transitioned from {current} to {target}";
            object payload = failure is not null
                ? new
                {
                    action = "StatusTransition",
                    from = current.ToString(),
                    to = target.ToString(),
                    validationStep = failure.Step.ToString(),
                    validationCode = failure.Code,
                    validationMessage = failure.Message,
                    error = failure,
                }
                : new
                {
                    action = "StatusTransition",
                    from = current.ToString(),
                    to = target.ToString(),
                };
            var details = JsonSerializer.SerializeToElement(payload);
            var severity = failure is not null
                ? Cvoya.Spring.Core.Capabilities.ActivitySeverity.Warning
                : Cvoya.Spring.Core.Capabilities.ActivitySeverity.Info;
            var ev = new Cvoya.Spring.Core.Capabilities.ActivityEvent(
                Id: Guid.NewGuid(),
                Timestamp: DateTimeOffset.UtcNow,
                Source: Address,
                EventType: Cvoya.Spring.Core.Capabilities.ActivityEventType.StateChanged,
                Severity: severity,
                Summary: summary,
                Details: details,
                CorrelationId: null,
                Cost: null);
            await activityEventBus.PublishAsync(ev);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "Agent {ActorId} StateChanged event publish failed (non-fatal).",
                Id.GetId());
        }

        return new TransitionResult(true, target, null);
    }

    /// <summary>
    /// Reads and clears the auto-start marker, then transitions the agent
    /// through <c>Stopped → Starting → Running</c>. Unlike
    /// <c>UnitActor.TryAutoStartAsync</c>, there is no connector-dispatcher
    /// hop — agents have no connector bindings in v0.1.
    /// </summary>
    private async Task TryAutoStartAsync(CancellationToken ct)
    {
        var pending = await StateManager.TryGetStateAsync<bool>(StateKeys.AgentPendingAutoStart, ct);
        if (!pending.HasValue || !pending.Value)
        {
            return;
        }

        // Clear the marker FIRST so a partial Running transition does not
        // leave a permanent auto-start flag that fires on every revalidation.
        await StateManager.TryRemoveStateAsync(StateKeys.AgentPendingAutoStart, ct);
        await StateManager.SaveStateAsync(ct);

        var startingResult = await TransitionAsync(LifecycleStatus.Starting, ct);
        if (!startingResult.Success)
        {
            _logger.LogWarning(
                "Agent {ActorId} auto-start skipped: Starting transition rejected: {Reason}",
                Id.GetId(), startingResult.RejectionReason);
            return;
        }

        var runningResult = await TransitionAsync(LifecycleStatus.Running, ct);
        if (!runningResult.Success)
        {
            _logger.LogWarning(
                "Agent {ActorId} auto-start: Running transition rejected: {Reason} (current status {Status})",
                Id.GetId(), runningResult.RejectionReason, runningResult.CurrentStatus);
        }
    }

    /// <summary>
    /// Determines whether this agent is a clone by checking for a stored <see cref="CloneIdentity"/>.
    /// </summary>
    internal async Task<bool> IsCloneAsync(CancellationToken cancellationToken = default)
    {
        var result = await StateManager
            .TryGetStateAsync<CloneIdentity>(StateKeys.CloneIdentity, cancellationToken);
        return result.HasValue;
    }

    /// <summary>
    /// Gets the clone identity of this agent, if it is a clone.
    /// </summary>
    internal async Task<CloneIdentity?> GetCloneIdentityAsync(CancellationToken cancellationToken = default)
    {
        var result = await StateManager
            .TryGetStateAsync<CloneIdentity>(StateKeys.CloneIdentity, cancellationToken);
        return result.HasValue ? result.Value : null;
    }

    /// <summary>
    /// Gets the parent agent ID if this agent is a clone, used for cost attribution.
    /// </summary>
    internal async Task<string?> GetCostAttributionTargetAsync(CancellationToken cancellationToken = default)
    {
        var identity = await GetCloneIdentityAsync(cancellationToken);
        return identity?.ParentAgentId;
    }

    /// <summary>
    /// Emits a pre-built <see cref="ActivityEvent"/> through the activity event bus.
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
        return stateCoordinator.GetMetadataAsync(Id.GetId(), cancellationToken);
    }

    /// <inheritdoc />
    public Task SetMetadataAsync(AgentMetadata metadata, CancellationToken cancellationToken = default)
    {
        return stateCoordinator.SetMetadataAsync(
            Id.GetId(),
            metadata,
            EmitActivityEventAsync,
            cancellationToken);
    }

    /// <summary>
    /// Legacy hook from the unit unassign endpoint. With ADR-0040 /
    /// #2048 the parent-unit pointer is no longer stored on the agent.
    /// </summary>
    public async Task ClearParentUnitAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Agent {AgentId} ClearParentUnit invoked; membership table is authoritative (ADR-0040).",
            Id.GetId());

        await EmitActivityEventAsync(
            ActivityEventType.StateChanged,
            "Agent parent-unit cleared",
            cancellationToken,
            details: JsonSerializer.SerializeToElement(new
            {
                action = "AgentParentUnitCleared",
            }));
    }

    /// <inheritdoc />
    public Task<ExpertiseDomain[]> GetExpertiseAsync(CancellationToken cancellationToken = default)
    {
        return stateCoordinator.GetExpertiseAsync(Id.GetId(), cancellationToken);
    }

    /// <inheritdoc />
    public Task SetExpertiseAsync(ExpertiseDomain[] domains, CancellationToken cancellationToken = default)
    {
        return stateCoordinator.SetExpertiseAsync(
            Id.GetId(),
            domains,
            EmitActivityEventAsync,
            cancellationToken);
    }

    /// <summary>
    /// Emits a <see cref="ActivityEventType.CostIncurred"/> event for this agent's execution costs.
    /// </summary>
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
    /// Records an observation for this agent.
    /// </summary>
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
    /// </summary>
    private async Task RegisterInitiativeReminderAsync(CancellationToken ct)
    {
        var registered = await StateManager
            .TryGetStateAsync<bool>(StateKeys.InitiativeReminderRegistered, ct);

        if (registered.HasValue && registered.Value)
        {
            return;
        }

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

/// <summary>
/// #2189: triple resolved by
/// <see cref="AgentActor.ClassifyAgentRuntimeException"/> and consumed
/// by the actor's runtime-issue producer. Internal so the unit tests
/// (in the same assembly via <c>[InternalsVisibleTo]</c>) can pin the
/// classification logic without depending on the actor's call sites.
/// </summary>
internal sealed record AgentRuntimeIssueClassification(string Source, string Code, string Title);
