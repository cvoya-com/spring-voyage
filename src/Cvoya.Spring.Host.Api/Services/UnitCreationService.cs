// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Security;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.Manifest;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IUnitCreationService"/> implementation.
///
/// The raw ingredients (directory register + actor metadata + member routing)
/// are identical to what <see cref="Endpoints.UnitEndpoints.CreateUnitAsync"/>
/// and <see cref="Endpoints.UnitEndpoints.AddMemberAsync"/> used to do inline;
/// this service just packages them so the three create endpoints share a path.
/// </summary>
public class UnitCreationService : IUnitCreationService
{
    /// <summary>
    /// Fallback creator identifier used when no authenticated principal is
    /// present on the ambient <see cref="HttpContext"/> — e.g. unit-testing
    /// contexts that spin the service up outside a request pipeline. Mirrors
    /// the synthetic <c>human://api</c> identity used elsewhere for
    /// platform-originated calls.
    /// </summary>
    public const string FallbackCreatorId = "api";

    private readonly IDirectoryService _directoryService;
    private readonly IActorProxyFactory _actorProxyFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IUnitConnectorConfigStore _connectorConfigStore;
    private readonly IReadOnlyList<IConnectorType> _connectorTypes;
    private readonly ISkillBundleResolver _bundleResolver;
    private readonly ISkillBundleValidator _bundleValidator;
    private readonly IUnitSkillBundleStore _bundleStore;
    private readonly IUnitMembershipRepository _membershipRepository;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUnitBoundaryStore? _boundaryStore;
    private readonly IOrchestrationStrategyCacheInvalidator _orchestrationCacheInvalidator;
    private readonly IUnitOrchestrationStore? _orchestrationStore;
    private readonly IUnitExecutionStore? _executionStore;
    private readonly IUnitMembershipTenantGuard? _tenantGuard;
    private readonly ILlmCredentialResolver? _credentialResolver;
    private readonly ILogger<UnitCreationService> _logger;

    /// <summary>
    /// Creates a new <see cref="UnitCreationService"/>. The
    /// <paramref name="boundaryStore"/> parameter is optional so existing test
    /// fixtures constructed before #494 landed keep compiling; when it is
    /// <c>null</c> manifest-declared boundaries are ignored with a warning.
    /// Production DI always supplies it via <see cref="IUnitBoundaryStore"/>.
    /// The <paramref name="orchestrationCacheInvalidator"/> parameter is
    /// optional so pre-#518 test fixtures keep compiling; production DI
    /// always supplies either the caching decorator or the no-op
    /// <see cref="NullOrchestrationStrategyCacheInvalidator"/>. When it is
    /// <c>null</c> the service falls back to the no-op behaviour.
    /// The <paramref name="orchestrationStore"/> parameter is optional for
    /// the same reason (#606 landed after #518) — when <c>null</c> the
    /// service falls back to the inline DB write that predated the store
    /// extraction so older test harnesses keep persisting strategy keys.
    /// </summary>
    public UnitCreationService(
        IDirectoryService directoryService,
        IActorProxyFactory actorProxyFactory,
        IHttpContextAccessor httpContextAccessor,
        IUnitConnectorConfigStore connectorConfigStore,
        IEnumerable<IConnectorType> connectorTypes,
        ISkillBundleResolver bundleResolver,
        ISkillBundleValidator bundleValidator,
        IUnitSkillBundleStore bundleStore,
        IUnitMembershipRepository membershipRepository,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
        IUnitBoundaryStore? boundaryStore = null,
        IOrchestrationStrategyCacheInvalidator? orchestrationCacheInvalidator = null,
        IUnitOrchestrationStore? orchestrationStore = null,
        IUnitExecutionStore? executionStore = null,
        IUnitMembershipTenantGuard? tenantGuard = null,
        ILlmCredentialResolver? credentialResolver = null)
    {
        _directoryService = directoryService;
        _actorProxyFactory = actorProxyFactory;
        _httpContextAccessor = httpContextAccessor;
        _connectorConfigStore = connectorConfigStore;
        _connectorTypes = connectorTypes.ToList();
        _bundleResolver = bundleResolver;
        _bundleValidator = bundleValidator;
        _bundleStore = bundleStore;
        _membershipRepository = membershipRepository;
        _scopeFactory = scopeFactory;
        _boundaryStore = boundaryStore;
        _orchestrationCacheInvalidator = orchestrationCacheInvalidator
            ?? NullOrchestrationStrategyCacheInvalidator.Instance;
        _orchestrationStore = orchestrationStore;
        _executionStore = executionStore;
        _tenantGuard = tenantGuard;
        _credentialResolver = credentialResolver;
        _logger = loggerFactory.CreateLogger<UnitCreationService>();
    }

    /// <inheritdoc />
    public Task<UnitCreationResult> CreateAsync(
        CreateUnitRequest request,
        CancellationToken cancellationToken)
    {
        // Review feedback on #744: every unit must have a parent. Either
        // the caller names ≥1 parent-unit ids OR passes `isTopLevel=true`
        // (parent = tenant). Neither / both → 400.
        var parentInfo = ValidateParentRequest(
            request.ParentUnitIds, request.IsTopLevel);

        return CreateCoreAsync(
            name: request.Name,
            displayName: request.DisplayName,
            description: request.Description,
            model: request.Model,
            color: request.Color,
            tool: request.Tool,
            provider: request.Provider,
            hosting: request.Hosting,
            members: Array.Empty<MemberManifest>(),
            warnings: new List<string>(),
            connector: request.Connector,
            skillReferences: Array.Empty<SkillBundleReference>(),
            // The direct-create path has always been last-writer-wins on
            // duplicate names; keep that behaviour so existing callers do
            // not observe a new 400. #325 introduces the duplicate check
            // specifically for the from-template override path.
            rejectDuplicates: false,
            parentInfo: parentInfo,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<UnitCreationResult> CreateFromManifestAsync(
        UnitManifest manifest,
        UnitCreationOverrides overrides,
        CancellationToken cancellationToken,
        UnitConnectorBindingRequest? connector = null)
    {
        // #325: the caller can override the manifest's name so repeated
        // template instantiations do not collide on the unique-name
        // constraint. An empty/whitespace override falls back to the manifest
        // name so existing callers remain unaffected.
        var name = !string.IsNullOrWhiteSpace(overrides.Name)
            ? overrides.Name!.Trim()
            : manifest.Name!;
        var displayName = !string.IsNullOrWhiteSpace(overrides.DisplayName)
            ? overrides.DisplayName!
            : name;
        var description = manifest.Description ?? string.Empty;
        var model = overrides.Model
            ?? manifest.Ai?.Model;
        var color = overrides.Color;

        var warnings = new List<string>();
        foreach (var section in ManifestParser.CollectUnsupportedSections(manifest))
        {
            warnings.Add(
                $"section '{section}' is parsed but not yet applied");
        }

        // #325: when the caller explicitly supplies a unit-name override we
        // reject duplicates up front with a 400. Manifest-name-only paths
        // keep the historical last-writer-wins behaviour to avoid changing
        // the /from-yaml and /from-template defaults unannounced.
        var rejectDuplicates = !string.IsNullOrWhiteSpace(overrides.Name);

        // Review feedback on #744: every unit must have a parent. Either
        // the overrides name ≥1 parent-unit ids OR pass `isTopLevel=true`
        // (parent = tenant). Neither / both → 400.
        var parentInfo = ValidateParentRequest(
            overrides.ParentUnitIds, overrides.IsTopLevel);

        var result = await CreateCoreAsync(
            name,
            displayName,
            description,
            model,
            color,
            overrides.Tool,
            overrides.Provider,
            overrides.Hosting,
            manifest.Members ?? new List<MemberManifest>(),
            warnings,
            connector,
            ExtractSkillReferences(manifest),
            rejectDuplicates,
            parentInfo,
            cancellationToken);

        // #488: persist the manifest's `expertise:` block onto the unit
        // definition row so the unit actor can auto-seed own-expertise from
        // it on first activation. Runs after CreateCoreAsync so the
        // UnitDefinitionEntity row already exists (the directory service
        // upserts it during RegisterAsync). Failures are non-fatal — the
        // unit is already live; the operator can push expertise via
        // `PUT /api/v1/units/{id}/expertise/own` if seed persistence hiccups.
        if (manifest.Expertise is { Count: > 0 })
        {
            await PersistUnitDefinitionExpertiseAsync(name, manifest.Expertise, cancellationToken);
        }

        // #491: persist the manifest's `orchestration.strategy` key onto the
        // unit definition row so the unit actor resolves the right keyed
        // IOrchestrationStrategy per message. Follows the same pattern as
        // the expertise seed above — both surfaces write to
        // `UnitDefinitions.Definition` because that is the single source of
        // declarative truth the actor reads. A missing / blank strategy
        // falls through to the policy-based inference and then the unkeyed
        // default, so we only bother writing when the caller actually
        // declared one.
        if (!string.IsNullOrWhiteSpace(manifest.Orchestration?.Strategy))
        {
            await PersistUnitDefinitionOrchestrationAsync(
                name, manifest.Orchestration!.Strategy!, cancellationToken);
        }

        // #494: persist the manifest's `boundary:` block through
        // IUnitBoundaryStore so the unit actor's boundary state matches what
        // a `PUT /api/v1/units/{id}/boundary` call would have produced. We
        // call the store directly (rather than writing to the Definition
        // JSON like expertise / orchestration) because boundary already has
        // a live persistence seam that the HTTP surface consumes — this
        // keeps YAML-applied and API-applied boundaries wire-identical. An
        // absent or all-empty block is a no-op so the unit's default
        // "transparent" view is preserved.
        if (manifest.Boundary is { IsEmpty: false })
        {
            await PersistUnitBoundaryAsync(name, manifest.Boundary, cancellationToken);
        }

        // #601 / #603 / #409 B-wide: persist the manifest's `execution:`
        // block through IUnitExecutionStore so the unit's execution
        // defaults match what a PUT /api/v1/units/{id}/execution call
        // would produce. An absent or all-empty block is a no-op so an
        // operator who clears the YAML doesn't re-apply a stale default.
        if (manifest.Execution is { IsEmpty: false })
        {
            await PersistUnitExecutionAsync(name, manifest.Execution, cancellationToken);
        }

        return result;
    }

    /// <summary>
    /// Writes the manifest <c>execution:</c> block onto the persisted
    /// <c>UnitDefinitions.Definition</c> JSON through the
    /// <see cref="IUnitExecutionStore"/> seam. Failures are non-fatal —
    /// the unit is already live; the operator can push the block via
    /// <c>PUT /api/v1/units/{id}/execution</c> if the write hiccups.
    /// </summary>
    private async Task PersistUnitExecutionAsync(
        string unitName,
        ExecutionManifest execution,
        CancellationToken cancellationToken)
    {
        if (_executionStore is null)
        {
            _logger.LogWarning(
                "Unit '{UnitName}': manifest declared an execution block but no IUnitExecutionStore is registered; skipping execution persistence.",
                unitName);
            return;
        }

        try
        {
            var defaults = new UnitExecutionDefaults(
                Image: execution.Image,
                Runtime: execution.Runtime,
                Tool: execution.Tool,
                Provider: execution.Provider,
                Model: execution.Model);
            await _executionStore.SetAsync(unitName, defaults, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Unit '{UnitName}': failed to persist execution block from manifest; unit remains with whatever execution defaults (if any) were previously configured.",
                unitName);
        }
    }

    /// <summary>
    /// Writes the manifest <c>expertise:</c> block onto the corresponding
    /// <see cref="Data.Entities.UnitDefinitionEntity.Definition"/> JSON so
    /// the unit actor's seed path picks it up on first activation. Idempotent:
    /// a subsequent manifest re-apply overwrites the expertise slot in place
    /// without touching other fields.
    /// </summary>
    private async Task PersistUnitDefinitionExpertiseAsync(
        string unitId,
        IReadOnlyList<ExpertiseManifestEntry> entries,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

            var entity = await db.UnitDefinitions
                .FirstOrDefaultAsync(u => u.UnitId == unitId && u.DeletedAt == null, cancellationToken);

            if (entity is null)
            {
                _logger.LogWarning(
                    "Unit '{UnitName}': could not locate UnitDefinition row to persist seed expertise; actor will activate without seed.",
                    unitId);
                return;
            }

            var shaped = entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Domain) || !string.IsNullOrWhiteSpace(e.Name))
                .Select(e => new
                {
                    domain = !string.IsNullOrWhiteSpace(e.Domain) ? e.Domain : e.Name,
                    description = e.Description,
                    level = e.Level,
                })
                .ToList();

            var payload = new Dictionary<string, object?> { ["expertise"] = shaped };

            // Preserve any other properties already on the Definition document
            // so we don't clobber a pre-existing instructions/execution block.
            if (entity.Definition is { ValueKind: System.Text.Json.JsonValueKind.Object } existing)
            {
                foreach (var prop in existing.EnumerateObject())
                {
                    if (!string.Equals(prop.Name, "expertise", StringComparison.OrdinalIgnoreCase))
                    {
                        payload[prop.Name] = prop.Value;
                    }
                }
            }

            entity.Definition = System.Text.Json.JsonSerializer.SerializeToElement(payload);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Unit '{UnitName}': failed to persist seed expertise on UnitDefinition; actor will activate without seed.",
                unitId);
        }
    }

    /// <summary>
    /// Writes the manifest <c>orchestration.strategy</c> key onto the
    /// corresponding <see cref="Data.Entities.UnitDefinitionEntity.Definition"/>
    /// JSON so the unit actor's <see cref="Cvoya.Spring.Core.Orchestration.IOrchestrationStrategyResolver"/>
    /// picks it up at dispatch time (#491). Idempotent: a subsequent
    /// manifest re-apply overwrites only the <c>orchestration</c> slot
    /// without touching other fields (including the <c>expertise</c> slot
    /// written by <see cref="PersistUnitDefinitionExpertiseAsync"/>).
    /// </summary>
    private async Task PersistUnitDefinitionOrchestrationAsync(
        string unitId,
        string strategyKey,
        CancellationToken cancellationToken)
    {
        try
        {
            // #606: prefer the shared IUnitOrchestrationStore when
            // registered so manifest-apply and the dedicated HTTP surface
            // stay wire-identical on persistence + cache-invalidation.
            // Older test fixtures that don't register the store fall
            // through to the inline DB path so they keep working.
            if (_orchestrationStore is not null)
            {
                await _orchestrationStore.SetStrategyKeyAsync(
                    unitId, strategyKey, cancellationToken);
                return;
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

            var entity = await db.UnitDefinitions
                .FirstOrDefaultAsync(u => u.UnitId == unitId && u.DeletedAt == null, cancellationToken);

            if (entity is null)
            {
                _logger.LogWarning(
                    "Unit '{UnitName}': could not locate UnitDefinition row to persist orchestration strategy; actor will resolve the default strategy.",
                    unitId);
                return;
            }

            var payload = new Dictionary<string, object?>
            {
                ["orchestration"] = new { strategy = strategyKey },
            };

            // Preserve any other properties already on the Definition document
            // so we don't clobber a pre-existing instructions / expertise /
            // execution block.
            if (entity.Definition is { ValueKind: System.Text.Json.JsonValueKind.Object } existing)
            {
                foreach (var prop in existing.EnumerateObject())
                {
                    if (!string.Equals(prop.Name, "orchestration", StringComparison.OrdinalIgnoreCase))
                    {
                        payload[prop.Name] = prop.Value;
                    }
                }
            }

            entity.Definition = System.Text.Json.JsonSerializer.SerializeToElement(payload);
            await db.SaveChangesAsync(cancellationToken);

            // #518: the provider's cache is authoritative within this
            // process for up to its TTL. Invalidate on successful write so
            // the next message dispatched to the unit sees the new strategy
            // immediately instead of waiting for the TTL to expire. Safe to
            // call unconditionally — the no-op implementation is registered
            // when no caching decorator is in play.
            _orchestrationCacheInvalidator.Invalidate(unitId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Unit '{UnitName}': failed to persist orchestration.strategy on UnitDefinition; actor will resolve the default strategy.",
                unitId);
        }
    }

    /// <summary>
    /// Projects the manifest's <c>boundary:</c> block to a core
    /// <see cref="UnitBoundary"/> and writes it through
    /// <see cref="IUnitBoundaryStore.SetAsync"/>. Idempotent: a subsequent
    /// manifest re-apply replaces every slot in place with the new shape.
    /// Failures are non-fatal — the unit is already live; the operator can
    /// push the boundary via <c>PUT /api/v1/units/{id}/boundary</c> if the
    /// store write hiccups.
    /// </summary>
    private async Task PersistUnitBoundaryAsync(
        string unitName,
        BoundaryManifest boundary,
        CancellationToken cancellationToken)
    {
        if (_boundaryStore is null)
        {
            _logger.LogWarning(
                "Unit '{UnitName}': manifest declared a boundary block but no IUnitBoundaryStore is registered; skipping boundary persistence.",
                unitName);
            return;
        }

        try
        {
            var core = ManifestBoundaryMapper.ToCore(boundary);
            if (core.IsEmpty)
            {
                // Every rule in the manifest was malformed (e.g. synthesis
                // entries with no name). Skip the write so we don't replace
                // an existing empty boundary with another one.
                return;
            }

            var address = new Address("unit", unitName);
            await _boundaryStore.SetAsync(address, core, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Unit '{UnitName}': failed to persist boundary from manifest; unit remains with whatever boundary (if any) was previously configured.",
                unitName);
        }
    }

    private static IReadOnlyList<SkillBundleReference> ExtractSkillReferences(UnitManifest manifest)
    {
        var references = manifest.Ai?.Skills;
        if (references is null || references.Count == 0)
        {
            return Array.Empty<SkillBundleReference>();
        }

        var list = new List<SkillBundleReference>(references.Count);
        foreach (var r in references)
        {
            if (string.IsNullOrWhiteSpace(r.Package) || string.IsNullOrWhiteSpace(r.Skill))
            {
                continue;
            }
            list.Add(new SkillBundleReference(r.Package!, r.Skill!));
        }
        return list;
    }

    private async Task<UnitCreationResult> CreateCoreAsync(
        string name,
        string displayName,
        string description,
        string? model,
        string? color,
        string? tool,
        string? provider,
        string? hosting,
        IReadOnlyList<MemberManifest> members,
        List<string> warnings,
        UnitConnectorBindingRequest? connector,
        IReadOnlyList<SkillBundleReference> skillReferences,
        bool rejectDuplicates,
        UnitParentInfo parentInfo,
        CancellationToken cancellationToken)
    {
        // Validate the connector binding request up-front — before we touch
        // any server-side state — so the caller sees a 400/404 without a
        // rollback dance happening under the hood.
        IConnectorType? targetConnector = null;
        if (connector is not null)
        {
            targetConnector = ResolveConnectorType(connector);
        }

        // Review feedback on #744: resolve every requested parent unit
        // BEFORE we touch any server-side state so the caller sees a
        // clean 404 with no partial-register rollback. Per-tenant visibility
        // is enforced through the tenant guard — cross-tenant parent-unit
        // ids surface as 404 so we never leak other-tenant units.
        var resolvedParents = new List<(string Id, DirectoryEntry Entry)>(parentInfo.ParentUnitIds.Count);
        foreach (var parentId in parentInfo.ParentUnitIds)
        {
            var parentAddress = new Address("unit", parentId);
            var parentEntry = await _directoryService.ResolveAsync(parentAddress, cancellationToken);
            if (parentEntry is null)
            {
                throw new UnknownParentUnitException(parentId);
            }
            if (_tenantGuard is not null)
            {
                // Ask the guard whether the parent is visible in the
                // current tenant. Matches the CreateAgent 404 shape so the
                // unit creation surface never leaks the existence of
                // other-tenant units.
                var visibleInTenant = await _tenantGuard.ShareTenantAsync(
                    parentAddress, parentAddress, cancellationToken);
                if (!visibleInTenant)
                {
                    throw new UnknownParentUnitException(parentId);
                }
            }
            resolvedParents.Add((parentId, parentEntry));
        }

        // Resolve skill bundles and validate their tool requirements up-front
        // as well. Any failure here surfaces to the caller as a typed
        // exception that the endpoint layer maps to a ProblemDetails 4xx so
        // the manifest author sees the exact bundle / tool that rejected
        // creation before we write any state.
        IReadOnlyList<SkillBundle> resolvedBundles = Array.Empty<SkillBundle>();
        if (skillReferences.Count > 0)
        {
            resolvedBundles = await ResolveSkillBundlesAsync(skillReferences, cancellationToken);
            var report = await _bundleValidator.ValidateAsync(name, resolvedBundles, cancellationToken);
            // Non-blocking warnings (e.g. bundles declaring tools no connector
            // surfaces) ride through the creation response's existing warnings
            // list so the wizard / CLI can surface them alongside manifest-
            // section warnings. Blocking problems throw from ValidateAsync.
            if (report.Warnings.Count > 0)
            {
                warnings.AddRange(report.Warnings);
            }
        }

        var actorId = Guid.NewGuid().ToString();
        var address = new Address("unit", name);

        // #325: when the caller supplies a canonical name override through
        // the request body we reject duplicates up front with a typed
        // exception the endpoint layer maps to 400. Paths that keep using
        // the manifest-derived name stay on the historical last-writer-wins
        // behaviour so #325 does not silently turn into a breaking change
        // for existing /from-yaml and /from-template callers.
        if (rejectDuplicates)
        {
            var existing = await _directoryService.ResolveAsync(address, cancellationToken);
            if (existing is not null)
            {
                throw new DuplicateUnitNameException(name);
            }
        }

        var entry = new DirectoryEntry(
            address,
            actorId,
            displayName,
            description,
            null,
            DateTimeOffset.UtcNow);

        await _directoryService.RegisterAsync(entry, cancellationToken);

        // Review feedback on #744: persist the IsTopLevel flag on the
        // UnitDefinition row so the parent-required invariant can
        // distinguish "deliberately tenant-parented" from "orphaned in
        // transit." The row was just upserted by RegisterAsync — load it
        // back and flip the flag in a separate save so we don't have to
        // reach into DirectoryService for this one field. A failure here
        // rolls the whole creation back (directory entry + any bundle
        // rows) via the catch below so the unit never exists in the
        // half-persisted state where its parent contract is ambiguous.
        if (parentInfo.IsTopLevel)
        {
            await SetTopLevelFlagAsync(name, cancellationToken);
        }

        try
        {
            // DisplayName/Description live on the directory entity; only forward
            // the actor-owned fields (Model, Color) to the metadata write to avoid
            // a double-write — mirrors UnitEndpoints.CreateUnitAsync.
            var metadata = new UnitMetadata(
                DisplayName: null,
                Description: null,
                Model: model,
                Color: color,
                Tool: tool,
                Provider: provider,
                Hosting: hosting);

            var proxy = _actorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(actorId), nameof(UnitActor));

            if (metadata.Model is not null || metadata.Color is not null
                || metadata.Tool is not null || metadata.Provider is not null
                || metadata.Hosting is not null)
            {
                await proxy.SetMetadataAsync(metadata, cancellationToken);
            }

            // Fix #324: grant the creator Owner on the brand-new unit BEFORE
            // any member-add runs. Without this grant, the unit has no
            // permission rows and any later router-dispatched call from the
            // same caller is denied at MessageRouter's `Viewer` gate. The
            // member adds below bypass the router (they are platform-internal
            // service-to-actor calls) so they don't need this grant, but the
            // creator will need it for every subsequent HTTP call they make
            // to this unit.
            var creatorGuid = await ResolveCreatorGuidAsync(cancellationToken);
            var creatorEntry = new UnitPermissionEntry(creatorGuid.ToString(), PermissionLevel.Owner);
            await proxy.SetHumanPermissionAsync(creatorGuid, creatorEntry, cancellationToken);

            // Mirror the grant onto the human actor's unit-scoped permission
            // map so both sides stay consistent — matches what
            // UnitEndpoints.SetHumanPermissionAsync does on direct PATCH.
            // HumanActor is keyed by UUID after #1491.
            try
            {
                var humanProxy = _actorProxyFactory.CreateActorProxy<IHumanActor>(
                    new ActorId(creatorGuid.ToString()), nameof(HumanActor));
                await humanProxy.SetPermissionForUnitAsync(name, PermissionLevel.Owner, cancellationToken);
            }
            catch (Exception ex)
            {
                // Non-fatal: the unit-side grant above is what the router's
                // permission check consults; the human-side mirror is purely
                // for symmetry with the PATCH endpoint. Log and move on so a
                // transient human-actor hiccup does not block creation.
                _logger.LogWarning(ex,
                    "Failed to mirror Owner grant onto human actor {HumanId} for unit '{UnitName}'; unit-side grant is authoritative.",
                    creatorGuid, name);
            }

            // Review feedback on #744: wire the new unit as a member of each
            // resolved parent unit BEFORE the manifest members are added. The
            // parent actors were resolved up-front (404 path above) so every
            // entry in resolvedParents maps to a reachable parent actor.
            // Failure to add to a parent rolls the whole creation back
            // (via the catch below) so a non-top-level unit is never left
            // unparented after CreateCoreAsync returns.
            foreach (var (parentId, parentEntry) in resolvedParents)
            {
                try
                {
                    var parentProxy = _actorProxyFactory.CreateActorProxy<IUnitActor>(
                        new ActorId(parentEntry.ActorId), nameof(UnitActor));
                    await parentProxy.AddMemberAsync(address, cancellationToken);
                }
                catch (Exception ex)
                {
                    throw new UnitCreationBindingException(
                        UnitCreationBindingFailureReason.StoreFailure,
                        $"Failed to attach unit '{name}' to parent unit '{parentId}': {ex.Message}",
                        ex);
                }
            }

            var membersAdded = 0;
            foreach (var member in members)
            {
                var resolved = ResolveMemberAddress(member);
                if (resolved is null)
                {
                    warnings.Add("member entry had no 'agent' or 'unit' field; skipped");
                    continue;
                }

                var memberAddress = new Address(resolved.Value.Scheme, resolved.Value.Path);

                // #745: for pre-existing members (not auto-registered below)
                // enforce the same-tenant invariant before the actor-state
                // write. Auto-registered agents are created in the current
                // tenant by DirectoryService.RegisterAsync so they need no
                // check — the guard only matters when the manifest names an
                // id that already exists. Unit-typed members follow the
                // same rule: the parent unit and the candidate must live
                // in the same tenant.
                if (_tenantGuard is not null)
                {
                    var existingDirectory = await _directoryService.ResolveAsync(
                        memberAddress, cancellationToken);
                    if (existingDirectory is not null)
                    {
                        var shareTenant = await _tenantGuard.ShareTenantAsync(
                            address, memberAddress, cancellationToken);
                        if (!shareTenant)
                        {
                            warnings.Add(
                                $"member {resolved.Value.Scheme}:{resolved.Value.Path} is not visible in this tenant; skipped");
                            continue;
                        }
                    }
                }

                // Fix #324: call the actor directly instead of round-tripping
                // through MessageRouter. The router's permission gate is for
                // external callers; a platform-internal service-to-actor call
                // does not belong behind it. The actor's own validation
                // (cycle detection etc.) still runs, and AddMemberAsync on
                // the actor emits the same StateChanged activity event the
                // router-dispatched domain message used to trigger.
                try
                {
                    await proxy.AddMemberAsync(
                        memberAddress,
                        cancellationToken);
                    membersAdded++;
                }
                catch (Exception ex)
                {
                    warnings.Add(
                        $"failed to add member {resolved.Value.Scheme}:{resolved.Value.Path}: {ex.Message}");
                    continue;
                }

                // Fix #340: the actor-state member list is no longer the
                // source of truth for the Agents tab, memberships endpoint,
                // and per-membership config — the unit_memberships table is
                // (see #245 / C2b-1). Mirror the add into the DB so template-
                // created units show up in those surfaces. Unit-typed members
                // remain 1:N and are not stored here (per #217 scope); only
                // agent-scheme members get a row. Template creation passes no
                // per-membership overrides so Model/Specialty/ExecutionMode
                // default to null and Enabled defaults to true.
                // After #1492, membership rows use UUID keys, so resolve both
                // the unit and agent slugs to their stable UUIDs first.
                if (string.Equals(resolved.Value.Scheme, "agent", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        // Resolve unit UUID from the newly-registered entry.
                        var unitDir = await _directoryService.ResolveAsync(address, cancellationToken);
                        var agentDir = await _directoryService.ResolveAsync(
                            new Address("agent", resolved.Value.Path), cancellationToken);

                        if (unitDir is not null && agentDir is not null
                            && Guid.TryParse(unitDir.ActorId, out var unitMemberUuid)
                            && Guid.TryParse(agentDir.ActorId, out var agentMemberUuid))
                        {
                            await _membershipRepository.UpsertAsync(
                                new UnitMembership(
                                    UnitId: unitMemberUuid,
                                    AgentId: agentMemberUuid,
                                    Enabled: true),
                                cancellationToken);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Unit '{UnitName}' member {Member}: could not resolve UUIDs for membership row; skipping DB write.",
                                name, $"{resolved.Value.Scheme}:{resolved.Value.Path}");
                            warnings.Add(
                                $"member {resolved.Value.Scheme}:{resolved.Value.Path} added to actor state but membership UUID resolution failed");
                        }
                    }
                    catch (Exception ex)
                    {
                        // The actor-state add succeeded; surface the DB-write
                        // failure as a warning and log it so operators can
                        // reconcile. Actor state remains the authoritative
                        // fast-path; a separate reconciler can repair any
                        // divergence.
                        _logger.LogWarning(ex,
                            "Unit '{UnitName}' member {Member}: actor-state add succeeded but membership DB write failed.",
                            name, $"{resolved.Value.Scheme}:{resolved.Value.Path}");
                        warnings.Add(
                            $"member {resolved.Value.Scheme}:{resolved.Value.Path} added to actor state but membership table write failed: {ex.Message}");
                    }

                    // Fix #374: auto-register agent-scheme members in the
                    // directory so they are discoverable via GET /api/v1/agents
                    // and the dashboard's Agents section. Idempotent — if the
                    // agent was already registered (e.g. via `spring agent
                    // create` before being added to the unit), the existing
                    // entry is preserved.
                    try
                    {
                        var agentAddress = new Address("agent", resolved.Value.Path);
                        var existing = await _directoryService.ResolveAsync(agentAddress, cancellationToken);
                        if (existing is null)
                        {
                            var agentActorId = Guid.NewGuid().ToString();
                            var agentEntry = new DirectoryEntry(
                                agentAddress,
                                agentActorId,
                                resolved.Value.Path,  // displayName = member name
                                string.Empty,          // description
                                null,                  // role
                                DateTimeOffset.UtcNow);
                            await _directoryService.RegisterAsync(agentEntry, cancellationToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Unit '{UnitName}' member {Member}: failed to auto-register agent directory entry.",
                            name, $"agent:{resolved.Value.Path}");
                        warnings.Add(
                            $"member agent:{resolved.Value.Path} added to unit but directory registration failed: {ex.Message}");
                    }
                }
            }

            // Persist the resolved skill bundles so prompt assembly can
            // rehydrate them on every message turn without reparsing the
            // manifest. Writes happen after the directory register so we
            // never leave bundle rows behind an un-discoverable unit.
            if (resolvedBundles.Count > 0)
            {
                await _bundleStore.SetAsync(name, resolvedBundles, cancellationToken);
            }

            // #947 / T-05: backend-validated creation. Direct-create
            // callers supply `model`/`provider`/`tool` on the request
            // body; mirror them onto the unit's execution block so the
            // scheduler can read back a consistent view of what to
            // validate against. The manifest path already writes this
            // through PersistUnitExecutionAsync.
            //
            // #1065: `Runtime` is the *container runtime* slot
            // (`docker | podman`) — never the LLM provider. The direct-
            // create request body carries no `--runtime` field (only
            // `unit execution set` does), so we leave `Runtime` null
            // here and let operators set it explicitly. Mirroring
            // `provider` into `Runtime` mislabels every unit created
            // with `--provider ollama` as needing the (non-existent)
            // `ollama` container runtime.
            if (_executionStore is not null &&
                (!string.IsNullOrWhiteSpace(model)
                    || !string.IsNullOrWhiteSpace(provider)
                    || !string.IsNullOrWhiteSpace(tool)))
            {
                try
                {
                    await _executionStore.SetAsync(
                        name,
                        new UnitExecutionDefaults(
                            Image: null,
                            Runtime: null,
                            Tool: tool,
                            Provider: provider,
                            Model: model),
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Unit '{UnitName}' failed to persist execution defaults on direct create; validation will not start.",
                        name);
                }
            }

            // When the request supplies a full execution config
            // (model + provider + a resolvable credential), transition
            // the unit straight into Validating so the Dapr
            // UnitValidationWorkflow can run the in-container probe.
            // Partial configs leave the unit in Draft — the user can
            // finish configuration and then call /revalidate (or
            // update + revalidate) to kick off validation.
            var initialStatus = UnitStatus.Draft;
            var fullyConfigured = await IsFullyConfiguredForValidationAsync(
                name, model, provider, cancellationToken);
            if (fullyConfigured)
            {
                try
                {
                    var transitionResult = await proxy.TransitionAsync(
                        UnitStatus.Validating, cancellationToken);
                    if (transitionResult is { Success: true })
                    {
                        initialStatus = UnitStatus.Validating;
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Unit '{UnitName}' failed to transition to Validating on creation: {Reason}. Staying in Draft.",
                            name, transitionResult?.RejectionReason ?? "unknown");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Unit '{UnitName}' transition to Validating failed on creation. Staying in Draft.",
                        name);
                }
            }

            // Bind the connector *after* the actor is reachable — the store
            // talks to the unit actor, which needs the directory entry in
            // place. A failure here rolls the whole creation back (below)
            // so the user never sees a half-configured unit.
            if (targetConnector is not null)
            {
                try
                {
                    await _connectorConfigStore.SetAsync(
                        name,
                        targetConnector.TypeId,
                        connector!.Config,
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    throw new UnitCreationBindingException(
                        UnitCreationBindingFailureReason.StoreFailure,
                        $"Failed to bind unit '{name}' to connector '{targetConnector.Slug}': {ex.Message}",
                        ex);
                }
            }

            var response = new UnitResponse(
                entry.ActorId,
                entry.Address.Path,
                entry.DisplayName,
                entry.Description,
                entry.RegisteredAt,
                initialStatus,
                metadata.Model,
                metadata.Color,
                metadata.Tool,
                metadata.Provider,
                metadata.Hosting);

            return new UnitCreationResult(response, warnings, membersAdded);
        }
        catch (UnitCreationBindingException)
        {
            await TryRollbackAsync(address, name, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Returns <c>true</c> when the unit has enough configuration to kick
    /// off backend validation at creation time: a model, a provider /
    /// runtime, and a resolvable credential (or a runtime that declares
    /// no credential is needed). Partial configs leave the unit in
    /// <see cref="UnitStatus.Draft"/> — the operator finishes
    /// configuration and then calls <c>/revalidate</c>.
    /// </summary>
    private async Task<bool> IsFullyConfiguredForValidationAsync(
        string unitName,
        string? model,
        string? provider,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(provider))
        {
            return false;
        }

        // Credential resolution is the last gate. When no resolver is
        // wired (legacy test harnesses), fall back to "model + provider
        // supplied == ready" which matches the pre-T-05 behaviour.
        if (_credentialResolver is null)
        {
            return true;
        }

        try
        {
            var resolution = await _credentialResolver.ResolveAsync(
                providerId: provider,
                unitName: unitName,
                cancellationToken);

            // A non-null value means we have a credential to hand to the
            // workflow. The "no credential required" path (Ollama) is
            // covered by the runtime-level filter inside the scheduler
            // and probe activities — it still reports NotFound here
            // because the secret resolver short-circuits on the empty
            // secret name. Treat NotFound as "we don't yet have enough
            // to probe" and leave the unit in Draft; users configure
            // credentials explicitly for that path.
            return !string.IsNullOrEmpty(resolution.Value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Unit '{UnitName}' credential resolution threw during creation; leaving unit in Draft.",
                unitName);
            return false;
        }
    }

    /// <summary>
    /// Best-effort rollback: unregisters the directory entry so the caller's
    /// failed creation leaves nothing behind. We deliberately do NOT touch
    /// the actor — absent a directory entry, its state is unreachable and
    /// will be cleared on the next actor reactivation. Unit-scoped secrets
    /// are not yet provisioned at this point (the connector binding is the
    /// last step), so no additional cleanup is needed.
    /// </summary>
    private async Task TryRollbackAsync(Address address, string name, CancellationToken ct)
    {
        try
        {
            await _directoryService.UnregisterAsync(address, ct);
        }
        catch (Exception ex)
        {
            // Surface but don't mask the original failure — the binding
            // exception is about to be rethrown. The operator sees both in
            // the logs.
            _logger.LogWarning(ex,
                "Rollback failed: could not unregister directory entry for unit '{UnitName}' after connector-binding failure. Manual cleanup may be required.",
                name);
        }

        // Best-effort bundle cleanup too — we may have persisted bundle rows
        // before the binding failure. A missing row is a no-op in the store.
        try
        {
            await _bundleStore.DeleteAsync(name, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Rollback failed: could not delete skill-bundle rows for unit '{UnitName}' after connector-binding failure.",
                name);
        }
    }

    /// <summary>
    /// Resolves every <see cref="SkillBundleReference"/> in declaration order,
    /// wrapping resolver exceptions in <see cref="SkillBundleValidationException"/>
    /// so the endpoint layer surfaces them through a single ProblemDetails
    /// mapping. The bundle order is preserved — the prompt layer concatenates
    /// prompts in declaration order per <c>docs/architecture/packages.md</c>.
    /// </summary>
    private async Task<IReadOnlyList<SkillBundle>> ResolveSkillBundlesAsync(
        IReadOnlyList<SkillBundleReference> references,
        CancellationToken ct)
    {
        var resolved = new List<SkillBundle>(references.Count);
        foreach (var reference in references)
        {
            resolved.Add(await _bundleResolver.ResolveAsync(reference, ct));
        }
        return resolved;
    }

    /// <summary>
    /// Looks up the requested connector type by id (preferred) or slug, and
    /// throws a typed exception when neither resolves. Also rejects requests
    /// that supply neither identifier.
    /// </summary>
    private IConnectorType ResolveConnectorType(UnitConnectorBindingRequest connector)
    {
        if (connector.TypeId == Guid.Empty && string.IsNullOrWhiteSpace(connector.TypeSlug))
        {
            throw new UnitCreationBindingException(
                UnitCreationBindingFailureReason.InvalidBindingRequest,
                "Connector binding requires either 'typeId' or 'typeSlug'.");
        }

        if (connector.TypeId != Guid.Empty)
        {
            var byId = _connectorTypes.FirstOrDefault(c => c.TypeId == connector.TypeId);
            if (byId is not null)
            {
                return byId;
            }
        }

        if (!string.IsNullOrWhiteSpace(connector.TypeSlug))
        {
            var bySlug = _connectorTypes.FirstOrDefault(
                c => string.Equals(c.Slug, connector.TypeSlug, StringComparison.OrdinalIgnoreCase));
            if (bySlug is not null)
            {
                return bySlug;
            }
        }

        var identifier = connector.TypeId != Guid.Empty
            ? connector.TypeId.ToString()
            : connector.TypeSlug!;
        throw new UnitCreationBindingException(
            UnitCreationBindingFailureReason.UnknownConnectorType,
            $"Connector '{identifier}' is not registered on this server.");
    }

    /// <summary>
    /// Resolves the stable UUID used to grant Owner on a freshly created unit.
    /// Reads the authenticated user's <c>NameIdentifier</c> claim and converts
    /// it to a UUID via <see cref="IHumanIdentityResolver"/> (upsert on first
    /// contact). Falls back to <see cref="FallbackCreatorId"/> when no
    /// authenticated principal is available — e.g. out-of-request contexts. In
    /// local-dev mode the LocalDev auth handler surfaces <c>local-dev-user</c>,
    /// so the grant lands on the right identity without needing the fallback.
    /// </summary>
    private async Task<Guid> ResolveCreatorGuidAsync(CancellationToken cancellationToken)
    {
        var username = FallbackCreatorId;
        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            var claim = user.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!string.IsNullOrWhiteSpace(claim))
            {
                username = claim;
            }
        }

        // Use a child scope so we get a fresh scoped IHumanIdentityResolver
        // even when UnitCreationService is registered as transient / singleton.
        await using var scope = _scopeFactory.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<IHumanIdentityResolver>();
        return await resolver.ResolveByUsernameAsync(username, null, cancellationToken);
    }

    private static (string Scheme, string Path)? ResolveMemberAddress(MemberManifest member)
    {
        if (!string.IsNullOrWhiteSpace(member.Agent))
        {
            return ("agent", member.Agent!);
        }
        if (!string.IsNullOrWhiteSpace(member.Unit))
        {
            return ("unit", member.Unit!);
        }
        return null;
    }

    /// <summary>
    /// Validates the caller-supplied parent inputs against the "every unit
    /// has a parent" invariant (review feedback on #744). Exactly one of
    /// <paramref name="parentUnitIds"/> or <paramref name="isTopLevel"/>
    /// must resolve to a positive signal; neither and both are rejected
    /// with <see cref="InvalidUnitParentRequestException"/>. Kept public so
    /// unit tests can exercise the pure classifier without spinning up the
    /// whole service graph.
    /// </summary>
    public static UnitParentInfo ValidateParentRequest(
        IReadOnlyList<string>? parentUnitIds,
        bool? isTopLevel)
    {
        var normalisedParents = (parentUnitIds ?? Array.Empty<string>())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var topLevel = isTopLevel ?? false;

        if (topLevel && normalisedParents.Count > 0)
        {
            throw new InvalidUnitParentRequestException(
                "Unit creation accepts either 'isTopLevel=true' or a non-empty 'parentUnitIds' list, not both. "
                + "Top-level units are parented by the tenant; attached units must name at least one parent unit.");
        }

        if (!topLevel && normalisedParents.Count == 0)
        {
            throw new InvalidUnitParentRequestException(
                "Unit creation must include either 'isTopLevel=true' or a non-empty 'parentUnitIds' list. "
                + "Every unit belongs to a parent — either another unit, or the tenant itself when top-level.");
        }

        return new UnitParentInfo(topLevel, normalisedParents);
    }

    private async Task SetTopLevelFlagAsync(string unitId, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetService<SpringDbContext>();
            if (db is null)
            {
                _logger.LogWarning(
                    "Unit '{UnitId}': SpringDbContext is not registered in the current scope; IsTopLevel flag will remain at its default (false).",
                    unitId);
                return;
            }

            var entity = await db.UnitDefinitions
                .FirstOrDefaultAsync(u => u.UnitId == unitId && u.DeletedAt == null, cancellationToken);

            if (entity is null)
            {
                _logger.LogWarning(
                    "Unit '{UnitId}': could not locate UnitDefinition row to persist IsTopLevel flag; flag will remain at its default (false).",
                    unitId);
                return;
            }

            entity.IsTopLevel = true;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Matches the PersistUnitDefinitionExpertiseAsync / boundary
            // patterns above: the unit itself is already live, so we
            // degrade to a warning rather than rolling back a successful
            // creation. Operators can re-apply the flag via a follow-up
            // path if the DB write hiccups — out of scope for this PR.
            _logger.LogWarning(ex,
                "Unit '{UnitId}': failed to persist IsTopLevel flag on UnitDefinition; flag will remain at its default (false).",
                unitId);
        }
    }
}

/// <summary>
/// Validated pair of "parent" inputs for <see cref="IUnitCreationService"/>.
/// Exactly one of the two will carry a positive signal: either
/// <see cref="IsTopLevel"/> is <c>true</c> and <see cref="ParentUnitIds"/>
/// is empty, or <see cref="ParentUnitIds"/> has at least one entry and
/// <see cref="IsTopLevel"/> is <c>false</c>.
/// </summary>
public sealed record UnitParentInfo(bool IsTopLevel, IReadOnlyList<string> ParentUnitIds);