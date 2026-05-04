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
using Cvoya.Spring.Dapr.Units;

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
    private readonly ILogger _logger;
    private readonly IOrchestrationStrategy _orchestrationStrategy;
    private readonly IActivityEventBus _activityEventBus;
    private readonly IDirectoryService _directoryService;
    private readonly IActorProxyFactory _actorProxyFactory;
    private readonly IExpertiseSeedProvider? _expertiseSeedProvider;
    private readonly IOrchestrationStrategyResolver? _strategyResolver;
    private readonly IUnitValidationCoordinator? _validationCoordinator;
    private readonly IUnitMembershipCoordinator _membershipCoordinator;
    private readonly IUnitPermissionCoordinator _permissionCoordinator;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnitActor"/> class.
    /// </summary>
    /// <param name="host">The actor host providing runtime services.</param>
    /// <param name="loggerFactory">The logger factory for creating loggers.</param>
    /// <param name="orchestrationStrategy">
    /// Default (unkeyed) strategy used to orchestrate domain messages when
    /// no manifest-declared strategy key is available. Kept on the
    /// constructor surface for backward compatibility with test harnesses
    /// that construct the actor directly; in production the resolver below
    /// is authoritative and this parameter acts as the last-resort fallback.
    /// </param>
    /// <param name="activityEventBus">The activity event bus for emitting observable events.</param>
    /// <param name="directoryService">Directory used to resolve <c>unit://</c> member paths to actor ids during cycle detection.</param>
    /// <param name="actorProxyFactory">Factory used to build <see cref="IUnitActor"/> proxies for sub-units during cycle detection.</param>
    /// <param name="expertiseSeedProvider">
    /// Optional provider that supplies seed <em>own</em>-expertise from the
    /// persisted <c>UnitDefinition</c> YAML on first activation (#488).
    /// Null in legacy test harnesses — seeding is skipped and the unit
    /// activates with whatever the state store holds.
    /// </param>
    /// <param name="strategyResolver">
    /// Optional manifest-driven strategy resolver (#491). When present,
    /// <see cref="HandleDomainMessageAsync"/> asks the resolver for the
    /// right <see cref="IOrchestrationStrategy"/> per message so the unit
    /// honours its declared <c>orchestration.strategy</c> key (and the
    /// inferred <c>label-routed</c> fallback when <c>UnitPolicy.LabelRouting</c>
    /// is present). Null in legacy test harnesses that construct the actor
    /// directly — the injected <paramref name="orchestrationStrategy"/>
    /// handles every message in that path, matching pre-#491 behaviour.
    /// </param>
    /// <param name="validationCoordinator">
    /// Optional coordinator for the validation-scheduling concern (#1280).
    /// When present, every transition into <see cref="UnitStatus.Validating"/>
    /// delegates to the coordinator to schedule the workflow and persist the
    /// run id; terminal callbacks are also routed through it. Null in legacy
    /// test harnesses that construct the actor directly — the transition still
    /// succeeds but no workflow is scheduled and no tracker writes occur.
    /// </param>
    /// <param name="subunitProjector">
    /// Optional write-through projector for the parent → child unit
    /// edge (#1154). Passed to <see cref="IUnitMembershipCoordinator"/>
    /// when constructing the default coordinator. When
    /// <paramref name="membershipCoordinator"/> is provided, this
    /// parameter is ignored and the caller-supplied coordinator governs
    /// projection. Null in legacy test harnesses; the actor-state list
    /// remains authoritative either way.
    /// </param>
    /// <param name="membershipCoordinator">
    /// Optional coordinator for the membership-management and
    /// cycle-detection concern (#1310). When present, every
    /// <see cref="AddMemberAsync"/> and <see cref="RemoveMemberAsync"/>
    /// call delegates entirely to the coordinator. When absent, a
    /// default <see cref="UnitMembershipCoordinator"/> is constructed
    /// from the remaining optional parameters so legacy test harnesses
    /// that construct the actor directly continue to work.
    /// </param>
    /// <param name="permissionCoordinator">
    /// Optional coordinator for the permission-management concern (#1311).
    /// When present, every <see cref="SetHumanPermissionAsync"/>,
    /// <see cref="GetHumanPermissionAsync"/>,
    /// <see cref="RemoveHumanPermissionAsync"/>,
    /// <see cref="GetHumanPermissionsAsync"/>,
    /// <see cref="GetPermissionInheritanceAsync"/>, and
    /// <see cref="SetPermissionInheritanceAsync"/> call delegates entirely
    /// to the coordinator. When absent, a default
    /// <see cref="UnitPermissionCoordinator"/> is constructed so legacy
    /// test harnesses that construct the actor directly continue to work.
    /// </param>
    public UnitActor(
        ActorHost host,
        ILoggerFactory loggerFactory,
        IOrchestrationStrategy orchestrationStrategy,
        IActivityEventBus activityEventBus,
        IDirectoryService directoryService,
        IActorProxyFactory actorProxyFactory,
        IExpertiseSeedProvider? expertiseSeedProvider = null,
        IOrchestrationStrategyResolver? strategyResolver = null,
        IUnitValidationWorkflowScheduler? validationWorkflowScheduler = null,
        IUnitValidationTracker? validationTracker = null,
        IUnitSubunitMembershipProjector? subunitProjector = null,
        IUnitValidationCoordinator? validationCoordinator = null,
        IUnitMembershipCoordinator? membershipCoordinator = null,
        IUnitPermissionCoordinator? permissionCoordinator = null)
        : base(host)
    {
        _logger = loggerFactory.CreateLogger<UnitActor>();
        _orchestrationStrategy = orchestrationStrategy;
        _activityEventBus = activityEventBus;
        _directoryService = directoryService;
        _actorProxyFactory = actorProxyFactory;
        _expertiseSeedProvider = expertiseSeedProvider;
        _strategyResolver = strategyResolver;
        _validationCoordinator = validationCoordinator
            ?? (validationWorkflowScheduler is not null || validationTracker is not null
                ? BuildDefaultValidationCoordinator(validationWorkflowScheduler, validationTracker, loggerFactory)
                : null);
        _membershipCoordinator = membershipCoordinator
            ?? new UnitMembershipCoordinator(
                subunitProjector,
                loggerFactory.CreateLogger<UnitMembershipCoordinator>());
        _permissionCoordinator = permissionCoordinator
            ?? new UnitPermissionCoordinator(
                loggerFactory.CreateLogger<UnitPermissionCoordinator>());
    }

    private static IUnitValidationCoordinator BuildDefaultValidationCoordinator(
        IUnitValidationWorkflowScheduler? scheduler,
        IUnitValidationTracker? tracker,
        ILoggerFactory loggerFactory)
        => new UnitValidationCoordinator(
            scheduler,
            tracker,
            loggerFactory.CreateLogger<UnitValidationCoordinator>());

    /// <summary>
    /// Gets the address of this unit actor.
    /// </summary>
    public Address Address => Address.For("unit", Id.GetId());

    /// <summary>
    /// Seeds the unit's own expertise from its <c>UnitDefinition</c> YAML on
    /// first activation (#488). Precedence rule: actor state is authoritative
    /// — the seed only applies when no own-expertise has been persisted to
    /// actor state yet (<see cref="StateKeys.UnitOwnExpertise"/> unset). Once
    /// an operator has PUT an expertise list (even an empty one), the unit
    /// never re-seeds from YAML so runtime edits survive process restarts.
    /// See <c>docs/architecture/units.md § Seeding from YAML</c>.
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
            var existing = await StateManager
                .TryGetStateAsync<List<ExpertiseDomain>>(StateKeys.UnitOwnExpertise, ct);

            // Actor state wins — if ANY value (including an empty list) was
            // persisted through SetOwnExpertiseAsync, the operator's runtime
            // edit is preserved across activations.
            if (existing.HasValue)
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
            await EmitActivityEventAsync(ActivityEventType.MessageReceived,
                MessageReceivedDetails.BuildSummary(message),
                ct,
                details: MessageReceivedDetails.Build(message),
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
                ct);

            return CreateErrorResponse(message, ex.Message);
        }
    }

    /// <inheritdoc />
    public async Task AddMemberAsync(Address member, CancellationToken ct = default)
    {
        await _membershipCoordinator.AddMemberAsync(
            unitActorId: Id.GetId(),
            unitAddress: Address,
            member: member,
            getMembers: GetMembersListAsync,
            persistMembers: async (list, c) =>
            {
                await StateManager.SetStateAsync(StateKeys.Members, list, c);
                await EmitActivityEventAsync(ActivityEventType.StateChanged,
                    $"Member {member} added to unit. Total members: {list.Count}",
                    c,
                    details: JsonSerializer.SerializeToElement(new
                    {
                        action = "MemberAdded",
                        member = $"{member.Scheme}://{member.Path}",
                        totalMembers = list.Count
                    }));
            },
            resolveAddress: (addr, c) => _directoryService.ResolveAsync(addr, c),
            getSubUnitMembers: async (actorId, c) =>
            {
                var proxy = _actorProxyFactory.CreateActorProxy<IUnitActor>(
                    new ActorId(actorId), nameof(UnitActor));
                return await proxy.GetMembersAsync(c);
            },
            cancellationToken: ct);
    }

    /// <inheritdoc />
    public async Task RemoveMemberAsync(Address member, CancellationToken ct = default)
    {
        await _membershipCoordinator.RemoveMemberAsync(
            unitActorId: Id.GetId(),
            member: member,
            getMembers: GetMembersListAsync,
            persistMembers: async (list, c) =>
            {
                await StateManager.SetStateAsync(StateKeys.Members, list, c);
                await EmitActivityEventAsync(ActivityEventType.StateChanged,
                    $"Member {member} removed from unit. Total members: {list.Count}",
                    c,
                    details: JsonSerializer.SerializeToElement(new
                    {
                        action = "MemberRemoved",
                        member = $"{member.Scheme}://{member.Path}",
                        totalMembers = list.Count
                    }));
            },
            cancellationToken: ct);
    }

    /// <inheritdoc />
    public async Task<Address[]> GetMembersAsync(CancellationToken ct = default)
    {
        var members = await GetMembersListAsync(ct);
        return members.ToArray();
    }

    /// <inheritdoc />
    public Task SetHumanPermissionAsync(Guid humanId, UnitPermissionEntry entry, CancellationToken ct = default)
        => _permissionCoordinator.SetHumanPermissionAsync(
            unitActorId: Id.GetId(),
            humanId: humanId,
            entry: entry,
            getPermissions: GetHumanPermissionsMapAsync,
            persistPermissions: (map, c) => StateManager.SetStateAsync(StateKeys.HumanPermissions, map, c),
            cancellationToken: ct);

    /// <inheritdoc />
    public Task<PermissionLevel?> GetHumanPermissionAsync(Guid humanId, CancellationToken ct = default)
        => _permissionCoordinator.GetHumanPermissionAsync(
            unitActorId: Id.GetId(),
            humanId: humanId,
            getPermissions: GetHumanPermissionsMapAsync,
            cancellationToken: ct);

    /// <inheritdoc />
    public Task<bool> RemoveHumanPermissionAsync(Guid humanId, CancellationToken ct = default)
        => _permissionCoordinator.RemoveHumanPermissionAsync(
            unitActorId: Id.GetId(),
            humanId: humanId,
            getPermissions: GetHumanPermissionsMapAsync,
            persistPermissions: (map, c) => StateManager.SetStateAsync(StateKeys.HumanPermissions, map, c),
            cancellationToken: ct);

    /// <inheritdoc />
    public Task<UnitPermissionEntry[]> GetHumanPermissionsAsync(CancellationToken ct = default)
        => _permissionCoordinator.GetHumanPermissionsAsync(
            unitActorId: Id.GetId(),
            getPermissions: GetHumanPermissionsMapAsync,
            cancellationToken: ct);

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

        var result = await PersistTransitionAsync(current, target, ct);

        // #947 / T-05: whenever the unit enters Validating we must schedule
        // the in-container probe workflow and persist its instance id so
        // the terminal callback can detect stale runs. The schedule + entity
        // write happens AFTER the state-store status write.
        // #1136: scheduling failure used to leave the unit stuck in
        // Validating; the coordinator now flips to Error on failure and
        // returns the post-recovery TransitionResult so the caller sees
        // the actual final state (Error) instead of the intermediate
        // Validating leg (#1280: this logic lives in UnitValidationCoordinator).
        if (result.Success && target == UnitStatus.Validating && _validationCoordinator is not null)
        {
            var recoveryResult = await _validationCoordinator.TryStartWorkflowAsync(
                Id.GetId(), PersistTransitionAsync, ct);
            if (recoveryResult is not null)
            {
                return recoveryResult;
            }
        }

        return result;
    }

    /// <inheritdoc />
    public async Task<TransitionResult> CompleteValidationAsync(
        UnitValidationCompletion completion, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(completion);

        // #1280: validation-completion logic lives in UnitValidationCoordinator.
        // When no coordinator is wired (legacy test harnesses that construct the
        // actor without scheduler/tracker), fall back to the minimal inline path
        // so older tests keep passing: read current status, guard against terminal
        // states, and apply the appropriate transition.
        if (_validationCoordinator is not null)
        {
            return await _validationCoordinator.CompleteValidationAsync(
                Id.GetId(),
                completion,
                GetStatusInternalAsync,
                PersistTransitionAsync,
                ct);
        }

        // Legacy fallback (no coordinator wired — e.g. UnitActorValidationTransitionTests
        // that construct the actor without a scheduler or tracker).
        var current = await GetStatusInternalAsync(ct);
        if (current == UnitStatus.Stopped || current == UnitStatus.Error)
        {
            return new TransitionResult(false, current, $"validation completion ignored: unit already {current}");
        }

        if (current != UnitStatus.Validating)
        {
            return new TransitionResult(false, current, $"validation completion ignored: status is {current}, expected Validating");
        }

        return await PersistTransitionAsync(
            UnitStatus.Validating,
            completion.Success ? UnitStatus.Stopped : UnitStatus.Error,
            ct);
    }

    /// <inheritdoc />
    public async Task<ReadinessResult> CheckReadinessAsync(CancellationToken ct = default)
    {
        var (isReady, missing) = await EvaluateReadinessAsync(ct);
        return new ReadinessResult(isReady, missing);
    }

    /// <inheritdoc />
    public async Task<ExpertiseDomain[]> GetOwnExpertiseAsync(CancellationToken ct = default)
    {
        var result = await StateManager
            .TryGetStateAsync<List<ExpertiseDomain>>(StateKeys.UnitOwnExpertise, ct);
        return result.HasValue ? result.Value.ToArray() : Array.Empty<ExpertiseDomain>();
    }

    /// <inheritdoc />
    public async Task SetOwnExpertiseAsync(ExpertiseDomain[] domains, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domains);

        // Store normalized copy — dedup by (Name, Level) so a caller that
        // PUTs duplicate domains does not bloat state.
        var normalised = domains
            .Where(d => !string.IsNullOrWhiteSpace(d.Name))
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();

        await StateManager.SetStateAsync(StateKeys.UnitOwnExpertise, normalised, ct);

        _logger.LogInformation(
            "Unit {ActorId} own expertise updated. Domain count: {Count}",
            Id.GetId(), normalised.Count);

        await EmitActivityEventAsync(ActivityEventType.StateChanged,
            $"Unit expertise updated. Domains: {normalised.Count}",
            ct,
            details: JsonSerializer.SerializeToElement(new
            {
                action = "UnitExpertiseUpdated",
                domains = normalised.Select(d => new { d.Name, d.Description, Level = d.Level?.ToString() }),
            }));
    }

    /// <inheritdoc />
    public async Task<UnitBoundary> GetBoundaryAsync(CancellationToken ct = default)
    {
        var result = await StateManager
            .TryGetStateAsync<UnitBoundary>(StateKeys.UnitBoundary, ct);
        return result.HasValue ? result.Value : UnitBoundary.Empty;
    }

    /// <inheritdoc />
    public async Task SetBoundaryAsync(UnitBoundary boundary, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(boundary);

        if (boundary.IsEmpty)
        {
            // Represent "no rules" as an absent row — the next read returns
            // UnitBoundary.Empty via the state-absent path, which is
            // semantically identical to an explicit empty boundary.
            await StateManager.RemoveStateAsync(StateKeys.UnitBoundary, ct);
        }
        else
        {
            await StateManager.SetStateAsync(StateKeys.UnitBoundary, boundary, ct);
        }

        _logger.LogInformation(
            "Unit {ActorId} boundary updated. Opacities: {OpacityCount}, Projections: {ProjectionCount}, Syntheses: {SynthesisCount}",
            Id.GetId(),
            boundary.Opacities?.Count ?? 0,
            boundary.Projections?.Count ?? 0,
            boundary.Syntheses?.Count ?? 0);

        await EmitActivityEventAsync(ActivityEventType.StateChanged,
            $"Unit boundary updated. Opacities={boundary.Opacities?.Count ?? 0}, Projections={boundary.Projections?.Count ?? 0}, Syntheses={boundary.Syntheses?.Count ?? 0}",
            ct,
            details: JsonSerializer.SerializeToElement(new
            {
                action = "UnitBoundaryUpdated",
                opacities = boundary.Opacities?.Count ?? 0,
                projections = boundary.Projections?.Count ?? 0,
                syntheses = boundary.Syntheses?.Count ?? 0,
            }));
    }

    /// <inheritdoc />
    public Task<UnitPermissionInheritance> GetPermissionInheritanceAsync(CancellationToken ct = default)
        => _permissionCoordinator.GetPermissionInheritanceAsync(
            unitActorId: Id.GetId(),
            getInheritance: async c =>
            {
                var result = await StateManager
                    .TryGetStateAsync<UnitPermissionInheritance>(StateKeys.UnitPermissionInheritance, c);
                return result.HasValue ? result.Value : null;
            },
            cancellationToken: ct);

    /// <inheritdoc />
    public async Task SetPermissionInheritanceAsync(UnitPermissionInheritance inheritance, CancellationToken ct = default)
    {
        await _permissionCoordinator.SetPermissionInheritanceAsync(
            unitActorId: Id.GetId(),
            inheritance: inheritance,
            persistInheritance: (value, c) => StateManager.SetStateAsync(StateKeys.UnitPermissionInheritance, value, c),
            removeInheritance: c => StateManager.RemoveStateAsync(StateKeys.UnitPermissionInheritance, c),
            cancellationToken: ct);

        await EmitActivityEventAsync(ActivityEventType.StateChanged,
            $"Unit permission inheritance updated to {inheritance}",
            ct,
            details: JsonSerializer.SerializeToElement(new
            {
                action = "UnitPermissionInheritanceUpdated",
                inheritance = inheritance.ToString(),
            }));
    }

    /// <inheritdoc />
    public async Task<UnitMetadata> GetMetadataAsync(CancellationToken ct = default)
    {
        var modelResult = await StateManager.TryGetStateAsync<string>(StateKeys.UnitModel, ct);
        var colorResult = await StateManager.TryGetStateAsync<string>(StateKeys.UnitColor, ct);
        var toolResult = await StateManager.TryGetStateAsync<string>(StateKeys.UnitTool, ct);
        var providerResult = await StateManager.TryGetStateAsync<string>(StateKeys.UnitProvider, ct);
        var hostingResult = await StateManager.TryGetStateAsync<string>(StateKeys.UnitHosting, ct);

        // DisplayName and Description are persisted on the directory entity,
        // not on the actor. See IUnitActor.GetMetadataAsync for the contract.
        // #1065: Tool / Provider / Hosting are actor-owned. Without these
        // reads the unit-detail GET returned them as null even when set on
        // create — see the side-note on #1065.
        return new UnitMetadata(
            DisplayName: null,
            Description: null,
            Model: modelResult.HasValue ? modelResult.Value : null,
            Color: colorResult.HasValue ? colorResult.Value : null,
            Tool: toolResult.HasValue ? toolResult.Value : null,
            Provider: providerResult.HasValue ? providerResult.Value : null,
            Hosting: hostingResult.HasValue ? hostingResult.Value : null);
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

        // #1065: persist Tool / Provider / Hosting so the unit-detail GET
        // round-trips them. Pre-fix the actor silently dropped these
        // fields, surfacing as `tool: null` / `provider: null` on
        // GET /api/v1/units/{id} despite being set on create.
        if (metadata.Tool is not null)
        {
            await StateManager.SetStateAsync(StateKeys.UnitTool, metadata.Tool, ct);
            writtenFields.Add(nameof(metadata.Tool));
        }

        if (metadata.Provider is not null)
        {
            await StateManager.SetStateAsync(StateKeys.UnitProvider, metadata.Provider, ct);
            writtenFields.Add(nameof(metadata.Provider));
        }

        if (metadata.Hosting is not null)
        {
            await StateManager.SetStateAsync(StateKeys.UnitHosting, metadata.Hosting, ct);
            writtenFields.Add(nameof(metadata.Hosting));
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
                tool = metadata.Tool,
                provider = metadata.Provider,
                hosting = metadata.Hosting,
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
    /// Persists a single status transition and emits the corresponding
    /// activity event. Shared by <see cref="TransitionAsync"/> and the
    /// <see cref="Cvoya.Spring.Dapr.Actors.UnitValidationCoordinator"/>.
    /// </summary>
    private async Task<TransitionResult> PersistTransitionAsync(
        UnitStatus current, UnitStatus target, CancellationToken ct)
    {
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

    /// <summary>
    /// Evaluates unit readiness. A unit must have a non-empty <c>Model</c>
    /// to leave Draft. Future requirements (members, connector) are
    /// documented but not yet enforced.
    /// </summary>
    private async Task<(bool IsReady, string[] Missing)> EvaluateReadinessAsync(CancellationToken ct)
    {
        var missing = new List<string>();

        var modelResult = await StateManager.TryGetStateAsync<string>(StateKeys.UnitModel, ct);
        if (!modelResult.HasValue || string.IsNullOrWhiteSpace(modelResult.Value))
        {
            missing.Add("model");
        }

        // Future requirements (document but don't enforce yet):
        // - At least one member (agent or sub-unit).
        // - Connector configured (if the template specifies one).

        return (missing.Count == 0, missing.ToArray());
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

            // Backend-validation edges (#944, T-02 / #939). Units enter
            // Validating from Draft (on creation) or Stopped/Error (on
            // /revalidate). The Dapr UnitValidationWorkflow drives the
            // Validating -> Stopped | Error transition via CompleteValidationAsync.
            // Draft -> Starting is intentionally absent: units must pass through
            // Validating before they can be started (#939).
            (UnitStatus.Draft, UnitStatus.Validating) => true,
            (UnitStatus.Validating, UnitStatus.Stopped) => true,
            (UnitStatus.Validating, UnitStatus.Error) => true,
            (UnitStatus.Error, UnitStatus.Validating) => true,
            (UnitStatus.Stopped, UnitStatus.Validating) => true,

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
        _logger.LogInformation("Unit {ActorId} received cancel for thread {ThreadId}",
            Id.GetId(), message.ThreadId);

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
            message.ThreadId,
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
    /// <remarks>
    /// When an <see cref="IOrchestrationStrategyResolver"/> is wired (the
    /// production path, #491), each message picks its strategy by reading
    /// the unit's declared <c>orchestration.strategy</c> key and, when
    /// absent, inferring <c>label-routed</c> from <c>UnitPolicy.LabelRouting</c>.
    /// The resolver owns a per-message DI scope so scoped strategies see
    /// fresh policy reads. The injected unkeyed
    /// <see cref="IOrchestrationStrategy"/> remains the final fallback for
    /// both test harnesses that construct the actor directly and units
    /// whose resolver resolved nothing.
    /// </remarks>
    private async Task<Message?> HandleDomainMessageAsync(Message message, CancellationToken ct)
    {
        var members = await GetMembersListAsync(ct);
        var context = new UnitContext(Address, members.AsReadOnly(), _logger);

        if (_strategyResolver is null)
        {
            _logger.LogInformation(
                "Unit {ActorId} delegating domain message {MessageId} to default orchestration strategy with {MemberCount} members",
                Id.GetId(), message.Id, members.Count);

            await EmitActivityEventAsync(ActivityEventType.DecisionMade,
                $"Delegating message {message.Id} to orchestration strategy with {members.Count} members",
                ct,
                details: JsonSerializer.SerializeToElement(new
                {
                    decision = "DelegateToStrategy",
                    messageId = message.Id,
                    memberCount = members.Count,
                }),
                correlationId: message.ThreadId);

            return await _orchestrationStrategy.OrchestrateAsync(message, context, ct);
        }

        await using var lease = await _strategyResolver.ResolveAsync(Id.GetId(), ct);

        _logger.LogInformation(
            "Unit {ActorId} delegating domain message {MessageId} to orchestration strategy '{StrategyKey}' with {MemberCount} members",
            Id.GetId(), message.Id, lease.ResolvedKey ?? "<default>", members.Count);

        await EmitActivityEventAsync(ActivityEventType.DecisionMade,
            $"Delegating message {message.Id} to orchestration strategy '{lease.ResolvedKey ?? "<default>"}' with {members.Count} members",
            ct,
            details: JsonSerializer.SerializeToElement(new
            {
                decision = "DelegateToStrategy",
                messageId = message.Id,
                memberCount = members.Count,
                strategyKey = lease.ResolvedKey,
            }),
            correlationId: message.ThreadId);

        return await lease.Strategy.OrchestrateAsync(message, context, ct);
    }

    /// <summary>
    /// Retrieves the current member list from state, returning an empty list if none exists.
    /// </summary>
    private async Task<List<Address>> GetMembersListAsync(CancellationToken ct)
    {
        var result = await StateManager
            .TryGetStateAsync<List<Address>>(StateKeys.Members, ct);

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