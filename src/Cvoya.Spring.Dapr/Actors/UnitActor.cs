// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Initiative;
using Cvoya.Spring.Dapr.Lifecycle;
using Cvoya.Spring.Dapr.Units;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

/// <summary>
/// Dapr virtual actor representing a unit in the Spring Voyage platform.
/// A unit groups agents and sub-units, dispatching domain messages through
/// the same runtime invocation path used by agents while handling
/// control messages (cancel, status, health, policy) directly.
/// </summary>
public class UnitActor : Actor, IUnitActor
{
    private readonly ILogger _logger;
    private readonly IRuntimeInvocationPath _runtimeInvocationPath;
    private readonly IActivityEventBus _activityEventBus;
    private readonly IDirectoryService _directoryService;
    private readonly IActorProxyFactory _actorProxyFactory;
    private readonly IExpertiseSeedProvider? _expertiseSeedProvider;
    private readonly IArtefactValidationCoordinator? _validationCoordinator;
    private readonly IUnitMembershipCoordinator _membershipCoordinator;
    private readonly IUnitStateCoordinator _stateCoordinator;
    private readonly IUnitMemberGraphStore _memberGraphStore;
    private readonly IUnitHumanPermissionStore? _humanPermissionStore;
    private readonly IUnitConnectorStartDispatcher? _connectorStartDispatcher;
    // #2160: producer-side seam for the operational-issues surface.
    // Optional so legacy test harnesses that construct the actor with a
    // partial dependency set still wire up — when null, transitions
    // simply don't publish issues, matching pre-#2160 behaviour.
    private readonly Cvoya.Spring.Core.Issues.IIssueWriter? _issueWriter;
    private readonly MessageArrivedDetails _messageArrivedDetails;
    // #2981: optional queryable mirror of this unit's lifecycle status.
    // Written on every transition (and on activation) so external gates and
    // the portal read-path see the status without racing the actor turn lock.
    // Optional so legacy in-memory unit tests that construct the actor with a
    // partial dependency set still wire up; production DI always supplies it.
    private readonly ILifecycleStatusStore? _lifecycleStatusStore;

    /// <summary>
    /// Per-thread dispatcher tracker — one entry per thread the unit is
    /// currently dispatching through <see cref="IRuntimeInvocationPath"/>
    /// (#2491). Surfaced through <see cref="GetRuntimeStatusAsync"/> so
    /// MCP discovery and the portal runtime-status endpoint see actual
    /// in-flight counts instead of the pre-#2491 hard-coded zero. Shared
    /// shape with <see cref="AgentActor"/> — both subjects sit on the
    /// same runtime-invocation seam per ADR-0039.
    /// </summary>
    private readonly ActorDispatchChannelTracker _activeWorkByThread = new();

    /// <summary>
    /// #3031: per-thread mailbox coordinator, shared verbatim with
    /// <see cref="AgentActor"/>. The coordinator is stateless across
    /// subjects (it operates entirely through the per-call channel
    /// delegates), so a unit drives the same enqueue / append / restart
    /// routing an agent does. Defaulted in the constructor when DI does not
    /// supply one so legacy in-memory unit tests still wire up.
    /// </summary>
    private readonly IAgentMailboxCoordinator _mailboxCoordinator;

    /// <summary>
    /// #3031: optional definition provider used solely to resolve the
    /// unit's <c>concurrent_threads</c> policy (mirrors
    /// <see cref="AgentActor"/>). When <c>null</c> the unit defaults to the
    /// platform default (<c>true</c>) — concurrent per-thread dispatch.
    /// </summary>
    private readonly IAgentDefinitionProvider? _agentDefinitionProvider;

    /// <summary>
    /// #3031: unit-wide serialisation lock used when the unit's
    /// <c>concurrent_threads</c> policy is <c>false</c>. Lazily created on
    /// the first dispatch; remains <c>null</c> under the default
    /// (<c>true</c>). Mirrors <see cref="AgentActor"/>'s agent-wide lock.
    /// </summary>
    private SemaphoreSlim? _unitWideLock;

    /// <summary>
    /// #3031: cached <c>concurrent_threads</c> value (resolved once; the
    /// flag is not editable at runtime). <c>null</c> means "not yet
    /// resolved". Mirrors <see cref="AgentActor"/>.
    /// </summary>
    private bool? _concurrentThreads;

    /// <summary>
    /// #3031: exposed for tests — the currently running dispatch task (if
    /// any). Production callers should not depend on this field. Mirrors
    /// <see cref="AgentActor.PendingDispatchTask"/>.
    /// </summary>
    internal Task? PendingDispatchTask { get; private set; }

    /// <summary>
    /// Initializes a new instance of the <see cref="UnitActor"/> class.
    /// </summary>
    /// <param name="host">The actor host providing runtime services.</param>
    /// <param name="loggerFactory">The logger factory for creating loggers.</param>
    /// <param name="runtimeInvocationPath">Shared runtime invocation pipeline for unit domain turns.</param>
    /// <param name="activityEventBus">The activity event bus for emitting observable events.</param>
    /// <param name="directoryService">Directory used to resolve <c>unit://</c> member paths to actor ids during cycle detection.</param>
    /// <param name="actorProxyFactory">Factory used to build <see cref="IUnitActor"/> proxies for sub-units during cycle detection.</param>
    /// <param name="expertiseSeedProvider">
    /// Optional provider that supplies seed <em>own</em>-expertise from the
    /// persisted <c>UnitDefinition</c> YAML on first activation (#488).
    /// Null in legacy test harnesses — seeding is skipped and the unit
    /// activates with whatever the state store holds.
    /// </param>
    /// <param name="validationCoordinator">
    /// Optional coordinator for the validation-scheduling concern (#1280).
    /// When present, every transition into <see cref="LifecycleStatus.Validating"/>
    /// delegates to the coordinator to schedule the workflow and persist the
    /// run id; terminal callbacks are also routed through it. Null in legacy
    /// test harnesses that construct the actor directly — the transition still
    /// succeeds but no workflow is scheduled and no tracker writes occur.
    /// </param>
    /// <param name="memberGraphStore">
    /// EF-backed singleton store over <c>unit_memberships</c> /
    /// <c>unit_subunit_memberships</c> (#2052 / ADR-0040). Required —
    /// the actor reads / writes the member graph through this seam on
    /// every call; there is no actor-state mirror.
    /// </param>
    /// <param name="membershipCoordinator">
    /// Optional coordinator for the membership-management and
    /// cycle-detection concern (#1310). When present, every
    /// <see cref="AddMemberAsync"/> and <see cref="RemoveMemberAsync"/>
    /// call delegates entirely to the coordinator. When absent, a
    /// default <see cref="UnitMembershipCoordinator"/> is constructed
    /// over <paramref name="memberGraphStore"/> so legacy test
    /// harnesses that construct the actor directly continue to work.
    /// </param>
    /// <param name="stateCoordinator">
    /// EF-backed coordinator for unit metadata, boundary, permission-
    /// inheritance, and own-expertise (#2049 / ADR-0040). Replaces the
    /// pre-#2049 <c>IUnitPermissionCoordinator</c>; the actor reads and
    /// writes through this seam on every metadata / boundary /
    /// inheritance / expertise call. Production DI always supplies the
    /// default; tests that construct the actor directly pass an
    /// in-memory test double (<c>InMemoryUnitLiveConfigStore</c>).
    /// </param>
    /// <param name="humanPermissionStore">
    /// Optional EF-backed store for (unit, human) ACL grants (#2044 /
    /// ADR-0040). When <c>null</c> the actor's grant operations fail
    /// closed: <see cref="SetHumanPermissionAsync"/> /
    /// <see cref="RemoveHumanPermissionAsync"/> throw
    /// <see cref="InvalidOperationException"/> and the read paths return
    /// empty. Production DI always supplies the default store; only legacy
    /// in-memory unit tests that construct the actor directly leave it
    /// <c>null</c>, in which case those tests must wire the store
    /// explicitly to exercise the ACL surface.
    /// </param>
    /// <param name="connectorStartDispatcher">
    /// Optional dispatcher for the unit-start connector hook (#2156). When
    /// non-<c>null</c> and the unit has been marked for auto-start (see
    /// <see cref="SetPendingAutoStartAsync"/>), a successful
    /// <see cref="CompleteValidationAsync"/> call transitions the unit
    /// through <c>Stopped → Starting → Running</c> in a single turn,
    /// invoking this dispatcher between <c>Starting</c> and <c>Running</c>
    /// so connectors get the same start hook the
    /// <c>POST /units/{id}/start</c> endpoint fires. Null in tests that
    /// don't register the host-side dispatcher — auto-start is skipped and
    /// the unit settles in <c>Stopped</c> exactly as the legacy
    /// behaviour did.
    /// </param>
    public UnitActor(
        ActorHost host,
        ILoggerFactory loggerFactory,
        IRuntimeInvocationPath runtimeInvocationPath,
        IActivityEventBus activityEventBus,
        IDirectoryService directoryService,
        IActorProxyFactory actorProxyFactory,
        IUnitStateCoordinator stateCoordinator,
        IUnitMemberGraphStore memberGraphStore,
        IExpertiseSeedProvider? expertiseSeedProvider = null,
        IArtefactValidationWorkflowScheduler? validationWorkflowScheduler = null,
        IArtefactValidationTracker? validationTracker = null,
        IArtefactValidationCoordinator? validationCoordinator = null,
        IUnitMembershipCoordinator? membershipCoordinator = null,
        IUnitHumanPermissionStore? humanPermissionStore = null,
        IUnitConnectorStartDispatcher? connectorStartDispatcher = null,
        Cvoya.Spring.Core.Issues.IIssueWriter? issueWriter = null,
        MessageArrivedDetails? messageArrivedDetails = null,
        ILifecycleStatusStore? lifecycleStatusStore = null,
        IAgentMailboxCoordinator? mailboxCoordinator = null,
        IAgentDefinitionProvider? agentDefinitionProvider = null)
        : base(host)
    {
        ArgumentNullException.ThrowIfNull(stateCoordinator);
        ArgumentNullException.ThrowIfNull(memberGraphStore);

        _logger = loggerFactory.CreateLogger<UnitActor>();
        _runtimeInvocationPath = runtimeInvocationPath;
        _activityEventBus = activityEventBus;
        _directoryService = directoryService;
        _actorProxyFactory = actorProxyFactory;
        _stateCoordinator = stateCoordinator;
        _memberGraphStore = memberGraphStore;
        _expertiseSeedProvider = expertiseSeedProvider;
        _validationCoordinator = validationCoordinator
            ?? (validationWorkflowScheduler is not null || validationTracker is not null
                ? BuildDefaultValidationCoordinator(validationWorkflowScheduler, validationTracker, loggerFactory)
                : null);
        _membershipCoordinator = membershipCoordinator
            ?? new UnitMembershipCoordinator(
                memberGraphStore,
                loggerFactory.CreateLogger<UnitMembershipCoordinator>());
        _humanPermissionStore = humanPermissionStore;
        _connectorStartDispatcher = connectorStartDispatcher;
        _issueWriter = issueWriter;
        _messageArrivedDetails = messageArrivedDetails ?? MessageArrivedDetails.Default;
        _lifecycleStatusStore = lifecycleStatusStore;
        // #3031: the mailbox coordinator is stateless across subjects, so a
        // default instance is equivalent to the DI singleton — mirrors how
        // _membershipCoordinator / _validationCoordinator default for legacy
        // test harnesses that construct the actor with a partial dependency set.
        _mailboxCoordinator = mailboxCoordinator
            ?? new AgentMailboxCoordinator(loggerFactory.CreateLogger<AgentMailboxCoordinator>());
        _agentDefinitionProvider = agentDefinitionProvider;
    }

    private static IArtefactValidationCoordinator BuildDefaultValidationCoordinator(
        IArtefactValidationWorkflowScheduler? scheduler,
        IArtefactValidationTracker? tracker,
        ILoggerFactory loggerFactory)
        => new ArtefactValidationCoordinator(
            scheduler,
            tracker,
            loggerFactory.CreateLogger<ArtefactValidationCoordinator>());

    /// <summary>
    /// Gets the address of this unit actor.
    /// </summary>
    public Address Address => Address.For("unit", Id.GetId());

    /// <summary>
    /// Seeds the unit's own expertise from its <c>UnitDefinition</c> YAML on
    /// first activation (#488). Precedence rule: operator-state wins — the
    /// seed only applies when no own-expertise has been persisted to EF yet
    /// (<c>unit_live_config.expertise_initialised</c> still <c>false</c>).
    /// Once an operator has PUT an expertise list (even an empty one), the
    /// flag flips to <c>true</c> and the unit never re-seeds from YAML so
    /// runtime edits survive process restarts. Per ADR-0040 / #2049 the
    /// flag lives on the <c>unit_live_config</c> EF row, not in actor
    /// state. See <c>docs/architecture/units.md § Seeding from YAML</c>.
    /// </summary>
    /// <remarks>
    /// Failures in seeding are non-fatal: the actor still activates and the
    /// operator can push the seed later via
    /// <c>PUT /api/v1/units/{id}/expertise/own</c>. The warning is logged so
    /// persistent seeding failures are visible in the observability pipeline.
    /// </remarks>
    protected override async Task OnActivateAsync()
    {
        await SeedOwnExpertiseFromDefinitionAsync(CancellationToken.None);

        // #2981: re-assert the authoritative status onto the queryable mirror
        // on activation so a unit that existed before the mirror column was
        // added (or whose mirror drifted) converges as soon as its actor next
        // activates. The store skips the write when unchanged, so this is free
        // in steady state. Guarded on the store so legacy test harnesses (no
        // store, no StateManager) don't touch actor state here.
        if (_lifecycleStatusStore is not null)
        {
            await WriteLifecycleMirrorAsync(
                await GetStatusInternalAsync(CancellationToken.None), CancellationToken.None);
        }

        await base.OnActivateAsync();
    }

    private async Task SeedOwnExpertiseFromDefinitionAsync(CancellationToken ct)
    {
        if (_expertiseSeedProvider is null)
        {
            return;
        }

        try
        {
            // ADR-0040 / #2049: the precedence flag lives on
            // unit_live_config.expertise_initialised. The activation-path
            // read is instrumented with a timing metric inside the store
            // so the v0.2 cache decision is data-driven (ADR-0040 § 3).
            var alreadyInitialised = await _stateCoordinator.HasOwnExpertiseSetAsync(Id.GetId(), ct);

            if (alreadyInitialised)
            {
                return;
            }

            var seed = await _expertiseSeedProvider.GetUnitSeedAsync(Id.GetId(), ct);
            if (seed is null || seed.Count == 0)
            {
                return;
            }

            await SetOwnExpertiseAsync(seed.ToArray(), ct);

            _logger.LogInformation(
                "Unit {ActorId} seeded own expertise from UnitDefinition YAML. Domain count: {Count}",
                Id.GetId(), seed.Count);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Unit {ActorId} failed to seed own expertise from UnitDefinition; activation proceeding with empty expertise.",
                Id.GetId());
        }
    }

    /// <inheritdoc />
    public async Task<Message?> ReceiveAsync(Message message, CancellationToken ct = default)
    {
        try
        {
            // correlationId carries the thread id so
            // IThreadQueryService (#452) can group every thread-related
            // event under the same thread row. #1209: stamp the
            // envelope (messageId, from, to, payload) onto Details so the
            // thread surfaces can render the body, not just the
            // summary.
            // #1636: the summary line is the actual message text (or a
            // short non-leaky placeholder) — never the legacy "Received
            // {Type} message <uuid> from <address>" envelope, which leaks
            // GUIDs into every downstream surface.
            await EmitActivityEventAsync(ActivityEventType.MessageArrived,
                _messageArrivedDetails.BuildSummary(message),
                ct,
                details: _messageArrivedDetails.Build(message),
                correlationId: message.ThreadId);

            return message.Type switch
            {
                MessageType.Cancel => await HandleCancelAsync(message, ct),
                MessageType.StatusQuery => await HandleStatusQueryAsync(ct),
                MessageType.HealthCheck => HandleHealthCheck(message),
                MessageType.PolicyUpdate => await HandlePolicyUpdateAsync(message, ct),
                MessageType.Domain => await HandleDomainMessageAsync(message, ct),
                _ => throw new CallerValidationException(
                    CallerValidationCodes.UnknownMessageType,
                    $"Unknown message type: {message.Type}")
            };
        }
        catch (Exception ex) when (ex is not SpringException)
        {
            _logger.LogError(ex,
                "Unhandled exception processing message {MessageId} of type {MessageType} in unit actor {ActorId}",
                message.Id, message.Type, Id.GetId());

            await EmitActivityEventAsync(ActivityEventType.ErrorOccurred,
                $"Error processing message {message.Id}: {ex.Message}",
                ct,
                details: JsonSerializer.SerializeToElement(new
                {
                    error = ex.Message,
                    agentId = Id.GetId(),
                    threadId = message.ThreadId,
                }),
                correlationId: message.ThreadId);

            return CreateErrorResponse(message, ex.Message);
        }
    }

    /// <inheritdoc />
    public Task AddMemberAsync(Address member, CancellationToken ct = default)
    {
        // ADR-0040 / #2052: the member graph lives in EF
        // (unit_memberships + unit_subunit_memberships); the coordinator
        // routes through IUnitMemberGraphStore. There is no actor-state
        // mirror — every read is a fresh EF query.
        var unitGuid = ParseSelfActorGuid();
        return _membershipCoordinator.AddMemberAsync(
            unitId: unitGuid,
            unitAddress: Address,
            member: member,
            emitStateChanged: (m, total, c) =>
                EmitActivityEventAsync(ActivityEventType.StateChanged,
                    $"Member {m} added to unit. Total members: {total}",
                    c,
                    details: JsonSerializer.SerializeToElement(new
                    {
                        action = "MemberAdded",
                        member = $"{m.Scheme}://{m.Path}",
                        totalMembers = total,
                    })),
            cancellationToken: ct);
    }

    /// <inheritdoc />
    public Task RemoveMemberAsync(Address member, CancellationToken ct = default)
    {
        // ADR-0040 / #2052: see AddMemberAsync — EF is the single store.
        var unitGuid = ParseSelfActorGuid();
        return _membershipCoordinator.RemoveMemberAsync(
            unitId: unitGuid,
            member: member,
            emitStateChanged: (m, total, c) =>
                EmitActivityEventAsync(ActivityEventType.StateChanged,
                    $"Member {m} removed from unit. Total members: {total}",
                    c,
                    details: JsonSerializer.SerializeToElement(new
                    {
                        action = "MemberRemoved",
                        member = $"{m.Scheme}://{m.Path}",
                        totalMembers = total,
                    })),
            cancellationToken: ct);
    }

    /// <inheritdoc />
    public async Task<Address[]> GetMembersAsync(CancellationToken ct = default)
    {
        var members = await GetMembersListAsync(ct);
        return [.. members];
    }

    /// <inheritdoc />
    public async Task SetHumanPermissionAsync(Guid humanId, UnitPermissionEntry entry, CancellationToken ct = default)
    {
        // #2044 / ADR-0040: ACL grants are EF rows, not actor state.
        // No actor-state read or write happens on this path — the
        // unit_human_permissions table is the sole source of truth.
        ArgumentNullException.ThrowIfNull(entry);
        var store = RequireHumanPermissionStore();
        var unitGuid = ParseSelfActorGuid();

        await store.UpsertAsync(unitGuid, humanId, entry, ct);

        _logger.LogInformation(
            "Unit {ActorId} granted permission {Permission} to human {HumanId}",
            Id.GetId(), entry.Permission, humanId);

        await EmitActivityEventAsync(ActivityEventType.StateChanged,
            $"Unit granted permission {entry.Permission} to human {humanId}",
            ct,
            details: JsonSerializer.SerializeToElement(new
            {
                action = "HumanPermissionGranted",
                humanId = humanId.ToString(),
                permission = entry.Permission.ToString(),
                identity = entry.Identity,
                notifications = entry.Notifications,
            }));
    }

    /// <inheritdoc />
    public async Task<PermissionLevel?> GetHumanPermissionAsync(Guid humanId, CancellationToken ct = default)
    {
        if (_humanPermissionStore is null)
        {
            return null;
        }

        var unitGuid = ParseSelfActorGuid();
        return await _humanPermissionStore.GetPermissionAsync(unitGuid, humanId, ct);
    }

    /// <inheritdoc />
    public async Task<bool> RemoveHumanPermissionAsync(Guid humanId, CancellationToken ct = default)
    {
        var store = RequireHumanPermissionStore();
        var unitGuid = ParseSelfActorGuid();

        var removed = await store.DeleteAsync(unitGuid, humanId, ct);
        if (!removed)
        {
            // Idempotent: the desired end state is "no entry for this
            // human on this unit". The CLI / endpoint stays a one-shot
            // and returns 204 regardless.
            return false;
        }

        _logger.LogInformation(
            "Unit {ActorId} removed permission for human {HumanId}",
            Id.GetId(), humanId);

        await EmitActivityEventAsync(ActivityEventType.StateChanged,
            $"Unit removed permission for human {humanId}",
            ct,
            details: JsonSerializer.SerializeToElement(new
            {
                action = "HumanPermissionRevoked",
                humanId = humanId.ToString(),
            }));

        return true;
    }

    /// <inheritdoc />
    public async Task<UnitPermissionEntry[]> GetHumanPermissionsAsync(CancellationToken ct = default)
    {
        if (_humanPermissionStore is null)
        {
            return Array.Empty<UnitPermissionEntry>();
        }

        var unitGuid = ParseSelfActorGuid();
        return await _humanPermissionStore.ListByUnitAsync(unitGuid, ct);
    }

    /// <summary>
    /// Returns the EF-backed permission store, throwing when the actor was
    /// constructed without one (legacy in-memory unit tests). Production DI
    /// always supplies the default store.
    /// </summary>
    private IUnitHumanPermissionStore RequireHumanPermissionStore()
        => _humanPermissionStore
            ?? throw new InvalidOperationException(
                "UnitActor was constructed without an IUnitHumanPermissionStore; ACL writes require the EF-backed store (#2044).");

    /// <summary>
    /// Parses this actor's id (the unit's stable Guid in 32-char no-dash hex
    /// form) back into a <see cref="Guid"/> for the EF row key.
    /// </summary>
    private Guid ParseSelfActorGuid()
        => Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(Id.GetId(), out var guid)
            ? guid
            : throw new InvalidOperationException(
                $"UnitActor activated with non-Guid id '{Id.GetId()}'; cannot key unit_human_permissions row.");

    /// <inheritdoc />
    public Task<LifecycleStatus> GetStatusAsync(CancellationToken ct = default)
        => GetStatusInternalAsync(ct);

    /// <inheritdoc />
    public async Task<TransitionResult> TransitionAsync(LifecycleStatus target, CancellationToken ct = default)
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

        var result = await PersistTransitionAsync(current, target, failure: null, ct);

        // #2981: entering a halted state (Stopping on operator stop, or Error)
        // cancels every in-flight dispatcher so an authoritative stop converges
        // immediately rather than letting the conversation drain. The receive
        // gate keeps new domain work from starting once the status is halted.
        // Idempotent — a no-op when nothing is in flight.
        if (result.Success && target.IsHalted())
        {
            await _activeWorkByThread.CancelAllAsync();
        }

        // #947 / T-05: whenever the unit enters Validating we must schedule
        // the in-container probe workflow and persist its instance id so
        // the terminal callback can detect stale runs. The schedule + entity
        // write happens AFTER the state-store status write.
        // #1136: scheduling failure used to leave the unit stuck in
        // Validating; the coordinator now flips to Error on failure and
        // returns the post-recovery TransitionResult so the caller sees
        // the actual final state (Error) instead of the intermediate
        // Validating leg (#1280: this logic lives in ArtefactValidationCoordinator).
        if (result.Success && target == LifecycleStatus.Validating && _validationCoordinator is not null)
        {
            var recoveryResult = await _validationCoordinator.TryStartWorkflowAsync(
                ArtefactKind.Unit, Id.GetId(), PersistTransitionAsync, ct);
            if (recoveryResult is not null)
            {
                return recoveryResult;
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<TransitionResult> CompleteValidationAsync(
        ArtefactValidationCompletion completion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(completion);

        // #1280: validation-completion logic lives in ArtefactValidationCoordinator.
        // When no coordinator is wired (legacy test harnesses that construct the
        // actor without scheduler/tracker), fall back to the minimal inline path
        // so older tests keep passing: read current status, guard against terminal
        // states, and apply the appropriate transition.
        TransitionResult result;
        if (_validationCoordinator is not null)
        {
            result = await _validationCoordinator.CompleteValidationAsync(
                ArtefactKind.Unit,
                Id.GetId(),
                completion,
                GetStatusInternalAsync,
                PersistTransitionAsync,
                ct);
        }
        else
        {
            // Legacy fallback (no coordinator wired — e.g. UnitActorValidationTransitionTests
            // that construct the actor without a scheduler or tracker).
            var current = await GetStatusInternalAsync(ct);
            if (current == LifecycleStatus.Stopped || current == LifecycleStatus.Error)
            {
                return new TransitionResult(false, current, $"validation completion ignored: unit already {current}");
            }

            if (current != LifecycleStatus.Validating)
            {
                return new TransitionResult(false, current, $"validation completion ignored: status is {current}, expected Validating");
            }

            result = await PersistTransitionAsync(
                LifecycleStatus.Validating,
                completion.Success ? LifecycleStatus.Stopped : LifecycleStatus.Error,
                completion.Success ? null : completion.Failure,
                ct);
        }

        // #2156: auto-start the unit after a successful validation when the
        // creation / package-install path marked it as pending. This makes
        // installed units land in Running without a manual click. The flag is
        // consumed once — subsequent revalidations leave the unit in Stopped
        // (the legacy /revalidate behaviour). The dispatcher is optional so
        // tests that construct the actor without it still see the
        // post-validation Stopped state.
        if (result.Success && result.CurrentStatus == LifecycleStatus.Stopped)
        {
            await TryAutoStartAsync(ct);
        }

        // #2160: publish the validation outcome to the operational-issues
        // surface so the Overview tab + CLI see this validation run as
        // a first-class issue. Producer-cleared model: a successful
        // transition to Stopped clears every prior validation-source
        // issue; a transition to Error opens an issue keyed on the
        // failure code. Best-effort — never let issue-publish failures
        // mask the validation outcome itself.
        if (_issueWriter is not null && result.Success)
        {
            await TryPublishValidationIssueAsync(result.CurrentStatus, completion.Failure, ct);
        }

        return result;
    }

    /// <summary>
    /// #2160: bridge the unit-validation outcome to the issue surface.
    /// </summary>
    private async Task TryPublishValidationIssueAsync(
        LifecycleStatus newStatus,
        Cvoya.Spring.Core.Lifecycle.ArtefactValidationError? failure,
        CancellationToken ct)
    {
        try
        {
            var subjectGuid = await ResolveOwnSubjectIdAsync(ct);
            if (subjectGuid is null)
            {
                return;
            }
            var subject = new Cvoya.Spring.Core.Issues.IssueSubject(
                Cvoya.Spring.Core.Issues.IssueSubjectKind.Unit,
                subjectGuid.Value);

            if (newStatus == Cvoya.Spring.Core.Lifecycle.LifecycleStatus.Error && failure is not null)
            {
                var title = string.IsNullOrWhiteSpace(failure.Message)
                    ? $"Validation failed at step {failure.Step}."
                    : failure.Message;
                await _issueWriter!.UpsertAsync(
                    subject,
                    Cvoya.Spring.Core.Issues.IssueSeverity.Error,
                    source: "validation",
                    code: failure.Code,
                    title: title,
                    detail: null,
                    traceId: null,
                    ct);
            }
            else
            {
                // Stopped / Running / etc. — validation cleared, drop
                // any prior validation-source issues this unit had.
                await _issueWriter!.ClearAsync(subject, source: "validation", code: null, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to publish validation issue for unit {UnitId}.",
                Id.GetId());
        }
    }

    private async Task<Guid?> ResolveOwnSubjectIdAsync(CancellationToken ct)
    {
        var address = Address.For("unit", Id.GetId());
        var entry = await _directoryService.ResolveAsync(address, ct);
        return entry?.ActorId;
    }

    /// <inheritdoc />
    public async Task SetPendingAutoStartAsync(CancellationToken ct = default)
    {
        // Idempotent — overwriting with the same value is fine; subsequent
        // SaveStateAsync persists the flag so the next CompleteValidationAsync
        // turn observes it.
        await StateManager.SetStateAsync(StateKeys.UnitPendingAutoStart, true, ct);
        await StateManager.SaveStateAsync(ct);
    }

    /// <summary>
    /// Reads and clears the auto-start marker, then transitions the unit
    /// through <c>Stopped → Starting → Running</c>, firing the connector
    /// start hook between the two transitions (#2156). A rejected Starting
    /// transition is logged and the unit is left in Stopped — the next
    /// manual <c>POST /units/{id}/start</c> can recover.
    /// </summary>
    private async Task TryAutoStartAsync(CancellationToken ct)
    {
        var pending = await StateManager.TryGetStateAsync<bool>(StateKeys.UnitPendingAutoStart, ct);
        if (!pending.HasValue || !pending.Value)
        {
            return;
        }

        // Clear the marker FIRST so a connector hook that throws or a
        // partial Running transition doesn't leave a permanent
        // auto-start flag re-firing on every revalidation.
        await StateManager.TryRemoveStateAsync(StateKeys.UnitPendingAutoStart, ct);
        await StateManager.SaveStateAsync(ct);

        if (_connectorStartDispatcher is null)
        {
            // No dispatcher wired (test harness). Leave the unit in
            // Stopped — the operator can still click Start manually and
            // the dispatcher endpoint will run.
            return;
        }

        var startingResult = await TransitionAsync(LifecycleStatus.Starting, ct);
        if (!startingResult.Success)
        {
            _logger.LogWarning(
                "Unit {ActorId} auto-start skipped: Starting transition rejected: {Reason}",
                Id.GetId(), startingResult.RejectionReason);
            return;
        }

        await _connectorStartDispatcher.DispatchAsync(Id.GetId(), ct);

        var runningResult = await TransitionAsync(LifecycleStatus.Running, ct);
        if (!runningResult.Success)
        {
            _logger.LogWarning(
                "Unit {ActorId} auto-start: Running transition rejected: {Reason} (current status {Status})",
                Id.GetId(), runningResult.RejectionReason, runningResult.CurrentStatus);
        }
    }

    /// <inheritdoc />
    public async Task<ReadinessResult> CheckReadinessAsync(CancellationToken ct = default)
    {
        var (isReady, missing) = await EvaluateReadinessAsync(ct);
        return new ReadinessResult(isReady, missing);
    }

    /// <inheritdoc />
    public Task<ExpertiseDomain[]> GetOwnExpertiseAsync(CancellationToken ct = default)
    {
        // ADR-0040 / #2049: own expertise reads come straight from the
        // unit_expertise EF table through the coordinator. The actor no
        // longer maintains an actor-state mirror.
        return _stateCoordinator.GetOwnExpertiseAsync(Id.GetId(), ct);
    }

    /// <inheritdoc />
    public Task SetOwnExpertiseAsync(ExpertiseDomain[] domains, CancellationToken ct = default)
    {
        // ADR-0040 / #2049: writes go to the unit_expertise EF table and
        // flip unit_live_config.expertise_initialised. The coordinator
        // emits a single StateChanged activity event after the write
        // commits.
        return _stateCoordinator.SetOwnExpertiseAsync(
            Id.GetId(),
            domains,
            EmitActivityEventAsync,
            ct);
    }

    /// <inheritdoc />
    public Task<UnitBoundary> GetBoundaryAsync(CancellationToken ct = default)
    {
        // ADR-0040 / #2049: boundary lives on the unit_live_config EF row.
        return _stateCoordinator.GetBoundaryAsync(Id.GetId(), ct);
    }

    /// <inheritdoc />
    public Task SetBoundaryAsync(UnitBoundary boundary, CancellationToken ct = default)
    {
        // ADR-0040 / #2049: write to the unit_live_config EF row.
        return _stateCoordinator.SetBoundaryAsync(
            Id.GetId(),
            boundary,
            EmitActivityEventAsync,
            ct);
    }

    /// <inheritdoc />
    public async Task<UnitPermissionInheritance> GetPermissionInheritanceAsync(CancellationToken ct = default)
    {
        // ADR-0040 / #2049: inheritance flag lives on the
        // unit_live_config EF row. Default of Inherit is applied inside
        // the repository.
        var ordinal = await _stateCoordinator.GetPermissionInheritanceAsync(Id.GetId(), ct);
        return (UnitPermissionInheritance)ordinal;
    }

    /// <inheritdoc />
    public Task SetPermissionInheritanceAsync(UnitPermissionInheritance inheritance, CancellationToken ct = default)
    {
        // ADR-0040 / #2049: write to the unit_live_config EF row. The
        // coordinator emits a StateChanged activity event after the
        // write commits.
        return _stateCoordinator.SetPermissionInheritanceAsync(
            Id.GetId(),
            (int)inheritance,
            EmitActivityEventAsync,
            ct);
    }

    /// <inheritdoc />
    public Task<UnitMetadata> GetMetadataAsync(CancellationToken ct = default)
    {
        // ADR-0040 / #2049: metadata reads come straight from the
        // unit_live_config EF row through the coordinator. The actor
        // no longer maintains an actor-state mirror. DisplayName /
        // Description live on the directory entity and are stitched in
        // by the API layer.
        return _stateCoordinator.GetMetadataAsync(Id.GetId(), ct);
    }

    /// <inheritdoc />
    public async Task SetMetadataAsync(UnitMetadata metadata, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        // ADR-0040 / #2049: writes go to the unit_live_config EF row.
        // The coordinator emits a StateChanged event for actor-owned
        // fields (Model / Color / Provider / Hosting). DisplayName /
        // Description live on the directory entity; we still emit a
        // separate audit event for them when they're the only fields
        // present, so the API layer's directory-only path retains a
        // visible activity row.
        await _stateCoordinator.SetMetadataAsync(
            Id.GetId(),
            metadata,
            EmitActivityEventAsync,
            ct);

        var directoryFields = new List<string>();
        if (metadata.DisplayName is not null) directoryFields.Add(nameof(metadata.DisplayName));
        if (metadata.Description is not null) directoryFields.Add(nameof(metadata.Description));

        var actorOwnedPresent =
            metadata.Model is not null
            || metadata.Color is not null
            || metadata.Provider is not null
            || metadata.Hosting is not null
            || metadata.Specialty is not null
            || metadata.Enabled is not null
            || metadata.ExecutionMode is not null;

        // The coordinator emits the StateChanged event when at least one
        // actor-owned field was patched. Emit a directory-only audit
        // event when ONLY directory fields are present so the API layer
        // does not need to duplicate the emission.
        if (!actorOwnedPresent && directoryFields.Count > 0)
        {
            await EmitActivityEventAsync(ActivityEventType.StateChanged,
                $"Unit metadata updated: {string.Join(", ", directoryFields)}",
                ct,
                details: JsonSerializer.SerializeToElement(new
                {
                    action = "UnitDirectoryMetadataUpdated",
                    directoryFields,
                    displayName = metadata.DisplayName,
                    description = metadata.Description,
                }));
        }
    }

    // ADR-0040 / #2050: GetConnectorBindingAsync /
    // SetConnectorBindingAsync / GetConnectorMetadataAsync /
    // SetConnectorMetadataAsync were removed from the actor surface.
    // Connector bindings + their runtime metadata live on the
    // unit_connector_bindings EF table; callers route through
    // IUnitConnectorConfigStore / IUnitConnectorRuntimeStore (the
    // public connector-package surface) or IUnitConnectorBindingStore
    // (the platform-internal seam used by the unit lifecycle endpoints).

    /// <summary>
    /// Persists a single status transition and emits the corresponding
    /// activity event. Shared by <see cref="TransitionAsync"/> and the
    /// <see cref="Cvoya.Spring.Dapr.Actors.ArtefactValidationCoordinator"/>.
    /// </summary>
    /// <remarks>
    /// #1665: when <paramref name="failure"/> is non-null (the
    /// Validating → Error case driven by
    /// <see cref="IArtefactValidationCoordinator"/>) the activity event's
    /// severity is elevated to <see cref="ActivitySeverity.Warning"/> and
    /// the validation <c>code</c>, <c>message</c>, and <c>step</c> are
    /// folded into the row's <c>summary</c> + <c>details</c>. Without
    /// this the Activity tab shows a bare "Unit transitioned from
    /// Validating to Error" line tagged Debug — invisible in the default
    /// feed and devoid of any cue as to *why* the validation failed.
    /// </remarks>
    private async Task<TransitionResult> PersistTransitionAsync(
        LifecycleStatus current,
        LifecycleStatus target,
        ArtefactValidationError? failure,
        CancellationToken ct)
    {
        await StateManager.SetStateAsync(StateKeys.UnitLifecycleStatus, target, ct);

        // #2981: mirror the just-persisted status onto the queryable
        // unit_live_config row so external gates (dispatcher cold-start,
        // message-router delivery) and the portal read-path see the new status
        // without racing this actor's turn lock — the read that previously
        // timed out and made the portal fabricate Starting. Best-effort: a
        // mirror-write failure must not fail the transition; actor state is
        // authoritative and the next activation re-syncs the mirror.
        await WriteLifecycleMirrorAsync(target, ct);

        _logger.LogInformation(
            "Unit {ActorId} transitioned from {Current} to {Target}",
            Id.GetId(), current, target);

        var summary = failure is not null
            ? $"Unit transitioned from {current} to {target}: {failure.Code} \u2014 {failure.Message}"
            : $"Unit transitioned from {current} to {target}";

        var details = failure is not null
            ? JsonSerializer.SerializeToElement(new
            {
                action = "StatusTransition",
                from = current.ToString(),
                to = target.ToString(),
                validationStep = failure.Step.ToString(),
                validationCode = failure.Code,
                validationMessage = failure.Message,
                error = failure,
            })
            : JsonSerializer.SerializeToElement(new
            {
                action = "StatusTransition",
                from = current.ToString(),
                to = target.ToString(),
            });

        var severityOverride = failure is not null
            ? (ActivitySeverity?)ActivitySeverity.Warning
            : null;

        await EmitActivityEventAsync(ActivityEventType.StateChanged,
            summary,
            ct,
            details: details,
            severityOverride: severityOverride);

        return new TransitionResult(true, target, null);
    }

    /// <summary>
    /// Evaluates unit readiness. A unit must have a non-empty <c>Model</c>
    /// to leave Draft. Future requirements (members, connector) are
    /// documented but not yet enforced.
    /// </summary>
    /// <remarks>
    /// ADR-0040 / #2049: <c>Model</c> lives on <c>unit_live_config</c>;
    /// the readiness probe consults the same coordinator the metadata
    /// surface uses so the answer is consistent with what
    /// <c>GetMetadataAsync</c> would return.
    /// </remarks>
    private async Task<(bool IsReady, string[] Missing)> EvaluateReadinessAsync(CancellationToken ct)
    {
        var missing = new List<string>();

        var metadata = await _stateCoordinator.GetMetadataAsync(Id.GetId(), ct);
        if (string.IsNullOrWhiteSpace(metadata.Model))
        {
            missing.Add("model");
        }

        // Future requirements (document but don't enforce yet):
        // - At least one member (agent or sub-unit).
        // - Connector configured (if the template specifies one).

        return (missing.Count == 0, missing.ToArray());
    }

    /// <summary>
    /// Reads the persisted lifecycle status, defaulting to <see cref="LifecycleStatus.Draft"/> when unset.
    /// </summary>
    private async Task<LifecycleStatus> GetStatusInternalAsync(CancellationToken ct)
    {
        var result = await StateManager
            .TryGetStateAsync<LifecycleStatus>(StateKeys.UnitLifecycleStatus, ct);

        return result.HasValue ? result.Value : LifecycleStatus.Draft;
    }

    /// <summary>
    /// #2981: writes <paramref name="status"/> to the queryable lifecycle
    /// mirror (<c>unit_live_config.lifecycle_status</c>). Best-effort and
    /// no-op when the store is unavailable (legacy test harnesses) or the id
    /// is not Guid-shaped — actor state remains authoritative.
    /// </summary>
    private async Task WriteLifecycleMirrorAsync(LifecycleStatus status, CancellationToken ct)
    {
        if (_lifecycleStatusStore is null
            || !GuidFormatter.TryParse(Id.GetId(), out var unitGuid))
        {
            return;
        }

        try
        {
            await _lifecycleStatusStore.SetStatusAsync(ArtefactKind.Unit, unitGuid, status, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to mirror lifecycle status {Status} for unit {UnitId}.",
                status, Id.GetId());
        }
    }

    /// <summary>
    /// Enforces the unit lifecycle state machine.
    /// </summary>
    private static bool IsTransitionAllowed(LifecycleStatus current, LifecycleStatus target) =>
        (current, target) switch
        {
            (LifecycleStatus.Draft, LifecycleStatus.Stopped) => true,
            (LifecycleStatus.Stopped, LifecycleStatus.Starting) => true,
            (LifecycleStatus.Starting, LifecycleStatus.Running) => true,
            (LifecycleStatus.Starting, LifecycleStatus.Error) => true,
            (LifecycleStatus.Running, LifecycleStatus.Stopping) => true,
            (LifecycleStatus.Stopping, LifecycleStatus.Stopped) => true,
            (LifecycleStatus.Stopping, LifecycleStatus.Error) => true,
            (LifecycleStatus.Error, LifecycleStatus.Stopped) => true,

            // Backend-validation edges (#944, T-02 / #939). Units enter
            // Validating from Draft (on creation) or Stopped/Error (on
            // /revalidate). The Dapr ArtefactValidationWorkflow drives the
            // Validating -> Stopped | Error transition via CompleteValidationAsync.
            // Draft -> Starting is intentionally absent: units must pass through
            // Validating before they can be started (#939).
            (LifecycleStatus.Draft, LifecycleStatus.Validating) => true,
            (LifecycleStatus.Validating, LifecycleStatus.Stopped) => true,
            (LifecycleStatus.Validating, LifecycleStatus.Error) => true,
            (LifecycleStatus.Error, LifecycleStatus.Validating) => true,
            (LifecycleStatus.Stopped, LifecycleStatus.Validating) => true,

            _ => false,
        };

    /// <summary>
    /// Handles a cancel message by cancelling the active dispatcher for
    /// the supplied thread, if any. Per ADR-0030 §44 the cancel is
    /// per-thread — other threads keep running.
    /// </summary>
    private async Task<Message?> HandleCancelAsync(Message message, CancellationToken ct)
    {
        var threadId = message.ThreadId;
        _logger.LogInformation("Unit {ActorId} received cancel for thread {ThreadId}",
            Id.GetId(), threadId);

        if (string.IsNullOrEmpty(threadId))
        {
            return CreateAckResponse(message);
        }

        // #3031: cancel any dispatcher running for this thread, then clear the
        // channel so a subsequent inbound on the same thread starts a fresh
        // drain loop. Mirrors AgentActor.HandleCancelAsync; per ADR-0030 §44
        // the cancel is per-thread — other threads' dispatchers are untouched.
        await _activeWorkByThread.CancelAsync(threadId);

        var channel = await GetChannelAsync(threadId, ct);
        if (channel is not null)
        {
            await RemoveChannelAsync(threadId, ct);
            _logger.LogInformation(
                "Unit {ActorId} cleared channel for cancelled thread {ThreadId}",
                Id.GetId(), threadId);
        }

        return CreateAckResponse(message);
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
            message.ThreadId,
            healthPayload,
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Handles a control-plane policy-update notification by emitting an
    /// audit event. ADR-0040 / #2049 collapses the actor-state
    /// <c>Unit:Policies</c> copy: the canonical
    /// <see cref="Cvoya.Spring.Dapr.Data.Entities.UnitPolicyEntity"/> row
    /// is the only write path. The notification carries no actor-state
    /// payload — it is the upstream signal that the EF policy row
    /// changed, so the actor just acknowledges and logs.
    /// </summary>
    private async Task<Message?> HandlePolicyUpdateAsync(Message message, CancellationToken ct)
    {
        _logger.LogInformation(
            "Unit {ActorId} received policy update notification (canonical row in unit_policies; no actor-state mirror)",
            Id.GetId());

        await EmitActivityEventAsync(ActivityEventType.StateChanged,
            "Unit policy update notification received",
            ct,
            details: JsonSerializer.SerializeToElement(new
            {
                action = "UnitPolicyUpdateNotified",
            }));

        return CreateAckResponse(message);
    }

    /// <summary>
    /// Handles a domain message by invoking the unit's runtime through the
    /// shared runtime path. Records the thread in
    /// <see cref="_activeWorkByThread"/> for the duration of the call so
    /// <see cref="GetRuntimeStatusAsync"/> reports a non-zero in-flight
    /// count while the runtime is dispatching (#2491).
    /// </summary>
    private async Task<Message?> HandleDomainMessageAsync(Message message, CancellationToken ct)
    {
        // #2981 / subsumed #2978: a stopped, stopping, or errored unit must
        // not invoke its runtime — the receive half of an authoritative stop.
        // Without this, an inbound message to a stopped unit cold-starts its
        // router container (resurrection) and keeps the in-flight conversation
        // alive. Drop the message with an audit event instead of dispatching.
        var lifecycleStatus = await GetStatusInternalAsync(ct);
        if (lifecycleStatus.IsHalted())
        {
            _logger.LogInformation(
                "Unit {ActorId} dropping domain message {MessageId} from {Sender}: lifecycle status is {Status}.",
                Id.GetId(), message.Id, message.From, lifecycleStatus);

            await EmitActivityEventAsync(
                ActivityEventType.DecisionMade,
                $"Dropped message {message.Id} from {message.From}: unit is {lifecycleStatus}.",
                ct,
                details: JsonSerializer.SerializeToElement(new
                {
                    decision = "ArtefactStopped",
                    lifecycleStatus = lifecycleStatus.ToString(),
                    sender = new { scheme = message.From.Scheme, path = message.From.Path },
                    messageId = message.Id,
                }),
                correlationId: message.ThreadId);

            return null;
        }

        _ = message.ThreadId
            ?? throw new CallerValidationException(
                CallerValidationCodes.MissingThreadId,
                "Domain messages must have a ThreadId");

        // #3031: enqueue into the per-thread mailbox and dispatch in the
        // background — mirrors AgentActor. ReceiveAsync returns as soon as the
        // message is enqueued, so a busy unit never blocks inbound delivery on
        // its runtime turn (the bug: the old inline `await InvokeAsync` held
        // the actor turn-lock for the entire 28–40 s turn, so synchronous
        // delivery to a busy unit timed out under #3004's per-attempt budget
        // and the same message was re-processed once per retry). The drain
        // loop (OnDispatchExitAsync) advances the per-thread FIFO queue when
        // each dispatch returns. Units carry no membership policy or
        // membership-disabled flag, so applyUnitPolicies is a pass-through and
        // the effective metadata is a minimal Enabled=true record; the
        // coordinator's own lifecycle gate is a no-op here because the halt
        // gate above already returned.
        await _mailboxCoordinator.HandleDomainMessageAsync(
            agentId: Id.GetId(),
            message: message,
            effective: new AgentMetadata(Enabled: true),
            lifecycleStatus: lifecycleStatus,
            applyUnitPolicies: (eff, _) => Task.FromResult((eff, (PolicyVerdict?)null)),
            getChannel: (tid, c) => GetChannelAsync(tid, c),
            saveChannel: (channel, c) => SaveChannelAsync(channel, c),
            dispatch: (channel, _, _) =>
            {
                var cts = _activeWorkByThread.Enter(channel.ThreadId);
                PendingDispatchTask = DispatchAsync(channel.Messages[0], cts.Token);
                return Task.CompletedTask;
            },
            emitActivity: EmitActivityEventAsync,
            cancellationToken: ct);
        return null;
    }

    /// <summary>
    /// Dispatches a single message for a per-thread channel (#3031). Mirrors
    /// <see cref="AgentActor"/>: when <c>concurrent_threads</c> is
    /// <c>false</c> the dispatch acquires the unit-wide lock first so
    /// concurrent threads run serialised; the lock release happens in
    /// <c>finally</c> so a cancelled or failing dispatch does not pin the
    /// unit. The mailbox-aware lean <see cref="IRuntimeInvocationPath"/>
    /// overload builds the unit's minimal prompt context internally (so unit
    /// runtime behaviour is unchanged) but threads the per-thread dispatch
    /// token and the drain callback so <see cref="OnDispatchExitAsync"/>
    /// advances the queue when the dispatcher returns.
    /// </summary>
    private async Task DispatchAsync(Message message, CancellationToken ct)
    {
        var threadId = message.ThreadId ?? string.Empty;
        var concurrent = await GetConcurrentThreadsAsync(ct);
        SemaphoreSlim? gate = concurrent ? null : GetOrCreateUnitWideLock();

        try
        {
            if (gate is not null)
            {
                await gate.WaitAsync(ct);
            }
            try
            {
                _logger.LogInformation(
                    "Unit {ActorId} invoking runtime path for domain message {MessageId}",
                    Id.GetId(), message.Id);

                await _runtimeInvocationPath.InvokeAsync(
                    Address,
                    message,
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
            // The gate.WaitAsync may throw on cancel before the dispatcher
            // gets a chance to run. Make sure the per-thread exit still runs
            // so the channel doesn't sit in a stuck Dispatching state. Other
            // catch-paths are owned by the dispatch coordinator and already
            // invoke onDispatchExit.
            await SignalDispatchExitViaSelfAsync(threadId, "dispatch cancelled before run");
        }
    }

    private SemaphoreSlim GetOrCreateUnitWideLock()
    {
        // First-write-wins; the actor turn model means there is at most one
        // concurrent caller into HandleDomainMessageAsync, so this does not
        // race in practice. Mirrors AgentActor.GetOrCreateAgentWideLock.
        return _unitWideLock ??= new SemaphoreSlim(1, 1);
    }

    /// <summary>
    /// Resolves the unit's <c>concurrent_threads</c> policy from the
    /// definition provider, caching the result for the actor's lifetime (the
    /// flag is not editable at runtime). Defaults to <c>true</c> (the
    /// platform default per ADR-0030 §3) when no provider is wired or the
    /// lookup fails. Mirrors <see cref="AgentActor"/>.
    /// </summary>
    private async Task<bool> GetConcurrentThreadsAsync(CancellationToken ct)
    {
        if (_concurrentThreads.HasValue)
        {
            return _concurrentThreads.Value;
        }

        if (_agentDefinitionProvider is null)
        {
            _concurrentThreads = true;
            return true;
        }

        try
        {
            var definition = await _agentDefinitionProvider.GetByIdAsync(Id.GetId(), ct);
            _concurrentThreads = definition?.Execution?.ConcurrentThreads ?? true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to resolve concurrent_threads for unit {ActorId}; defaulting to true.",
                Id.GetId());
            _concurrentThreads = true;
        }

        return _concurrentThreads.Value;
    }

    private async Task SignalDispatchExitViaSelfAsync(string threadId, string reason)
    {
        // The dispatcher runs outside the actor turn, so we can't touch
        // StateManager directly. Self-call through Dapr remoting so the call
        // queues on this actor's turn. In tests where the proxy factory is a
        // substitute that returns null, fall back to a direct call — the test
        // harness mocks StateManager so the off-turn race the proxy guards
        // against doesn't apply. Mirrors AgentActor.SignalDispatchExitViaSelfAsync.
        try
        {
            var self = _actorProxyFactory.CreateActorProxy<IUnitActor>(Id, nameof(UnitActor));
            if (self is not null)
            {
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
                "Failed to signal dispatch exit for unit {ActorId} thread {ThreadId} (reason: {Reason}).",
                Id.GetId(), threadId, reason);
        }
    }

    /// <inheritdoc />
    public async Task OnDispatchExitAsync(
        string threadId,
        string? reason,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(threadId))
        {
            _logger.LogWarning(
                "Unit {ActorId} OnDispatchExitAsync called without a thread id (reason: {Reason}).",
                Id.GetId(), reason);
            return;
        }

        var channel = await GetChannelAsync(threadId, ct);
        if (channel is null)
        {
            // Channel was already cleared (e.g. by a per-thread cancel).
            _activeWorkByThread.Exit(threadId);
            _logger.LogDebug(
                "Unit {ActorId} OnDispatchExitAsync no-op for thread {ThreadId} (reason: {Reason}).",
                Id.GetId(), threadId, reason);
            return;
        }

        // Remove the head message that was just dispatched; subsequent appends
        // have queued behind it, so removing the head preserves per-thread FIFO.
        if (channel.Messages.Count > 0)
        {
            channel.Messages.RemoveAt(0);
        }

        _activeWorkByThread.Exit(threadId);

        // #2981: if the unit was stopped while this dispatcher was running,
        // quiesce instead of re-arming the drain loop — drop the remaining
        // queued messages (authoritative stop = drop, not hold). Mirrors
        // AgentActor.
        var lifecycleStatus = await GetStatusInternalAsync(ct);
        if (lifecycleStatus.IsHalted())
        {
            await RemoveChannelAsync(threadId, ct);
            _logger.LogInformation(
                "Unit {ActorId} thread {ThreadId} drain quiesced: lifecycle status is {Status} (reason: {Reason})",
                Id.GetId(), threadId, lifecycleStatus, reason);
            return;
        }

        if (channel.Messages.Count == 0)
        {
            // Drain complete — remove the channel so a subsequent inbound on
            // the same thread starts fresh.
            await RemoveChannelAsync(threadId, ct);
            _logger.LogInformation(
                "Unit {ActorId} thread {ThreadId} drain complete (reason: {Reason})",
                Id.GetId(), threadId, reason);
            return;
        }

        // Drain the next queued message: re-mark dispatching, save the channel
        // with the head removed, and fire a fresh dispatcher for the new head.
        channel.Dispatching = true;
        await SaveChannelAsync(channel, ct);

        var head = channel.Messages[0];
        var newCts = _activeWorkByThread.Enter(threadId);
        PendingDispatchTask = DispatchAsync(head, newCts.Token);
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

    /// <summary>
    /// Retrieves the current member graph from EF
    /// (<c>unit_memberships</c> + <c>unit_subunit_memberships</c>). ADR-0040
    /// / #2052: there is no actor-state mirror; every read goes through
    /// <see cref="IUnitMemberGraphStore"/>.
    /// </summary>
    private async Task<IReadOnlyList<Address>> GetMembersListAsync(CancellationToken ct)
    {
        var unitGuid = ParseSelfActorGuid();
        return await _memberGraphStore.GetMembersAsync(unitGuid, ct);
    }

    /// <inheritdoc />
    public async Task<Cvoya.Spring.Core.Agents.AgentRuntimeStatusReport> GetRuntimeStatusAsync(
        CancellationToken ct = default)
    {
        // #3031: derive the snapshot from per-thread ThreadChannel state,
        // matching AgentActor.GetRuntimeStatusAsync. In-flight := channels
        // with Dispatching == true; queued := total messages minus the
        // in-flight heads (a Dispatching channel with N messages contributes
        // N-1 queued); a non-dispatching channel that still holds messages is
        // a transient state between drains and counts all its messages as
        // queued. The API layer combines this with the PersistentAgentRegistry
        // health probe to project `busy` / `queued` / `idle` / `unavailable`
        // (#2100).
        var threadIds = await GetChannelIndexAsync(ct);
        var inFlight = 0;
        var queued = 0;
        var channelCount = 0;

        foreach (var tid in threadIds)
        {
            var channel = await GetChannelAsync(tid, ct);
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

        return new Cvoya.Spring.Core.Agents.AgentRuntimeStatusReport(
            InFlightThreadCount: inFlight,
            QueuedMessageCount: queued,
            ChannelCount: channelCount,
            ObservedAt: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Publishes a pre-built <see cref="ActivityEvent"/> to the activity
    /// bus. Failures are logged but never allowed to escape the actor
    /// turn. This overload matches the
    /// <c>Func&lt;ActivityEvent, CancellationToken, Task&gt;</c> delegate
    /// shape coordinator seams (e.g. <see cref="IUnitStateCoordinator"/>)
    /// expect, so the actor can pass it as a method group without an
    /// adapter.
    /// </summary>
    private async Task EmitActivityEventAsync(ActivityEvent activityEvent, CancellationToken cancellationToken)
    {
        try
        {
            await _activityEventBus.PublishAsync(activityEvent, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to emit activity event {EventType} for unit actor {ActorId}.",
                activityEvent.EventType, Id.GetId());
        }
    }

    /// <summary>
    /// Emits an activity event through the activity event bus.
    /// Failures are logged but never allowed to escape the actor turn.
    /// </summary>
    /// <remarks>
    /// <paramref name="severityOverride"/>, when set, takes precedence
    /// over the <paramref name="eventType"/>-driven default — used by
    /// <see cref="PersistTransitionAsync"/> to promote a validation-failure
    /// transition above the default <c>Debug</c> level so the row is
    /// visually distinct in the Activity feed (#1665).
    /// </remarks>
    private async Task EmitActivityEventAsync(
        ActivityEventType eventType,
        string description,
        CancellationToken cancellationToken,
        JsonElement? details = null,
        string? correlationId = null,
        ActivitySeverity? severityOverride = null)
    {
        try
        {
            var severity = severityOverride ?? (eventType switch
            {
                ActivityEventType.ErrorOccurred => ActivitySeverity.Error,
                ActivityEventType.StateChanged => ActivitySeverity.Debug,
                _ => ActivitySeverity.Info,
            });

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
