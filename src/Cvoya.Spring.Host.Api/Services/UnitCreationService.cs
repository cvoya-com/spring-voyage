// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.Manifest;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

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
    private readonly IAuthenticatedCallerAccessor _callerAccessor;
    private readonly IUnitConnectorConfigStore _connectorConfigStore;
    private readonly IReadOnlyList<IConnectorType> _connectorTypes;
    private readonly ISkillBundleResolver _bundleResolver;
    private readonly ISkillBundleValidator _bundleValidator;
    private readonly IUnitSkillBundleStore _bundleStore;
    private readonly IUnitMemberGraphStore _memberGraphStore;
    private readonly ITenantContext _tenantContext;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IUnitBoundaryStore? _boundaryStore;
    private readonly IUnitExecutionStore? _executionStore;
    private readonly IUnitMembershipTenantGuard? _tenantGuard;
    private readonly ILlmCredentialResolver? _credentialResolver;
    private readonly IRuntimeCatalog? _runtimeCatalog;
    private readonly IArtefactAutoStartGate? _autoStartGate;
    private readonly ILogger<UnitCreationService> _logger;

    /// <summary>
    /// Creates a new <see cref="UnitCreationService"/>. The
    /// <paramref name="boundaryStore"/> parameter is optional so existing test
    /// fixtures constructed before #494 landed keep compiling; when it is
    /// <c>null</c> manifest-declared boundaries are ignored with a warning.
    /// Production DI always supplies it via <see cref="IUnitBoundaryStore"/>.
    /// </summary>
    public UnitCreationService(
        IDirectoryService directoryService,
        IActorProxyFactory actorProxyFactory,
        IAuthenticatedCallerAccessor callerAccessor,
        IUnitConnectorConfigStore connectorConfigStore,
        IEnumerable<IConnectorType> connectorTypes,
        ISkillBundleResolver bundleResolver,
        ISkillBundleValidator bundleValidator,
        IUnitSkillBundleStore bundleStore,
        IUnitMemberGraphStore memberGraphStore,
        ITenantContext tenantContext,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
        IUnitBoundaryStore? boundaryStore = null,
        IUnitExecutionStore? executionStore = null,
        IUnitMembershipTenantGuard? tenantGuard = null,
        ILlmCredentialResolver? credentialResolver = null,
        IRuntimeCatalog? runtimeCatalog = null,
        IArtefactAutoStartGate? autoStartGate = null)
    {
        ArgumentNullException.ThrowIfNull(memberGraphStore);
        ArgumentNullException.ThrowIfNull(tenantContext);

        _directoryService = directoryService;
        _actorProxyFactory = actorProxyFactory;
        _callerAccessor = callerAccessor;
        _connectorConfigStore = connectorConfigStore;
        _connectorTypes = connectorTypes.ToList();
        _bundleResolver = bundleResolver;
        _bundleValidator = bundleValidator;
        _bundleStore = bundleStore;
        _memberGraphStore = memberGraphStore;
        _tenantContext = tenantContext;
        _scopeFactory = scopeFactory;
        _boundaryStore = boundaryStore;
        _executionStore = executionStore;
        _tenantGuard = tenantGuard;
        _credentialResolver = credentialResolver;
        _runtimeCatalog = runtimeCatalog;
        _autoStartGate = autoStartGate;
        _logger = loggerFactory.CreateLogger<UnitCreationService>();
    }

    /// <inheritdoc />
    public async Task<UnitCreationResult> CreateAsync(
        CreateUnitRequest request,
        CancellationToken cancellationToken)
    {
        // Review feedback on #744: every unit must have a parent. Either
        // the caller names ≥1 parent-unit ids OR passes `isTopLevel=true`
        // (parent = tenant). Neither / both → 400.
        var parentInfo = ValidateParentRequest(
            request.ParentUnitIds, request.IsTopLevel);

        var result = await CreateCoreAsync(
            name: request.Name,
            displayName: request.DisplayName,
            description: request.Description,
            model: request.Model,
            color: request.Color,
            // ADR-0038: provider is intrinsic to the structured execution.model.
            // The Unit row no longer carries a flat provider slot.
            provider: null,
            hosting: request.Hosting,
            role: null,
            specialty: null,
            enabled: null,
            executionMode: null,
            members: Array.Empty<MemberManifest>(),
            warnings: new List<string>(),
            connector: request.Connector,
            skillReferences: Array.Empty<SkillBundleReference>(),
            // The direct-create path has always been last-writer-wins on
            // duplicate names; keep that behaviour so existing callers do
            // not observe a new 400. #325 introduces the duplicate check
            // for the manifest-backed path (rejectDuplicates: true there).
            rejectDuplicates: false,
            parentInfo: parentInfo,
            preMintedActorId: null,
            cancellationToken);

        // #2204: evaluate the auto-start gate after all persistence is done.
        // Direct-create callers typically have no execution config at this
        // point (no `ai.runtime` / `image`), so the gate stays closed and the
        // unit lands in Draft — same as before. Operators that supply
        // execution config separately can then call /revalidate (which keeps
        // its legacy "settle in Stopped" behaviour) or future flows that
        // configure execution atomically at create time.
        var promotedStatus = await TryAutoStartValidationAsync(
            result.Unit.Id, request.DisplayName, cancellationToken);
        if (promotedStatus != result.Unit.Status)
        {
            result = result with { Unit = result.Unit with { Status = promotedStatus } };
        }
        return result;
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
        // Display-name precedence (single-source-of-truth for both top-level
        // and nested artefacts):
        //   1. Operator's per-target override (only flowed for the package's
        //      single top-level activatable from PackageInstallService).
        //   2. Manifest's `displayName:` field (declarative slot; null when
        //      the YAML omits it).
        //   3. Canonical `name:` field — preserves pre-displayName behaviour
        //      when neither (1) nor (2) is set.
        var displayName = !string.IsNullOrWhiteSpace(overrides.DisplayName)
            ? overrides.DisplayName!
            : (!string.IsNullOrWhiteSpace(manifest.DisplayName)
                ? manifest.DisplayName!
                : name);
        var description = manifest.Description ?? string.Empty;
        // ADR-0038: ai.model is structured {provider, id}; the Model
        // hint surfaced on the unit row is the provider-scoped id.
        var model = overrides.Model
            ?? manifest.Ai?.Model?.Id;
        var color = !string.IsNullOrWhiteSpace(overrides.Color) ? overrides.Color : manifest.Color;

        // Per-unit configuration metadata declared in the manifest. These
        // mirror UpdateUnitRequest fields so the YAML authoring surface and
        // the PATCH /api/v1/units/{id} surface agree on the same set of
        // configurable knobs.
        var role = manifest.Role;
        var specialty = manifest.Specialty;
        var enabled = manifest.Enabled;
        AgentExecutionMode? executionMode = null;
        if (!string.IsNullOrWhiteSpace(manifest.ExecutionMode)
            && Enum.TryParse<AgentExecutionMode>(manifest.ExecutionMode, ignoreCase: true, out var parsedExecMode))
        {
            executionMode = parsedExecMode;
        }

        var warnings = new List<string>();
        foreach (var section in ManifestParser.CollectUnsupportedSections(manifest))
        {
            warnings.Add(
                $"section '{section}' is parsed but not yet applied");
        }

        // #325: when the caller explicitly supplies a unit-name override we
        // reject duplicates up front with a 400. Manifest-name-only paths
        // keep the historical last-writer-wins behaviour.
        var rejectDuplicates = !string.IsNullOrWhiteSpace(overrides.Name);

        // Review feedback on #744: every unit must have a parent. Either
        // the overrides name ≥1 parent-unit ids OR pass `isTopLevel=true`
        // (parent = tenant). Neither / both → 400.
        var parentInfo = ValidateParentRequest(
            overrides.ParentUnitIds, overrides.IsTopLevel);

        // Issue #2436: forward the manifest's `execution.hosting` to the
        // unit-actor live config when the operator didn't supply an
        // explicit override. Manifest-declared hosting flows onto the
        // unit's UnitMetadata.Hosting the same way `model` flows from
        // `ai.model.id`; member agents that lack their own hosting then
        // inherit it through the activator's stamp step (precedence:
        // agent > template > unit > default `persistent`). The manifest
        // parser has already normalised the literal to lower-case and
        // rejected unknown values, so the value reaches the store
        // canonical.
        var hosting = !string.IsNullOrWhiteSpace(overrides.Hosting)
            ? overrides.Hosting
            : manifest.Execution?.Hosting;

        var result = await CreateCoreAsync(
            name,
            displayName,
            description,
            model,
            color,
            overrides.Provider,
            hosting,
            role,
            specialty,
            enabled,
            executionMode,
            manifest.Members ?? new List<MemberManifest>(),
            warnings,
            connector,
            ExtractSkillReferences(manifest),
            rejectDuplicates,
            parentInfo,
            overrides.ActorId,
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

        // Persist the manifest's `instructions:` block so the unit's
        // Instructions slot is populated at install time (same seam as the
        // PATCH /api/v1/units/{id} `instructions` field). Template-derived
        // instructions land here after the TemplateResolver has merged them.
        if (!string.IsNullOrEmpty(manifest.Instructions))
        {
            await PersistUnitDefinitionInstructionsAsync(result.Unit.Id, manifest.Instructions, cancellationToken);
        }

        // #494: persist the manifest's `boundary:` block through
        // IUnitBoundaryStore so the unit actor's boundary state matches what
        // a `PUT /api/v1/units/{id}/boundary` call would have produced. We
        // call the store directly (rather than writing to the Definition
        // JSON like expertise / execution) because boundary already has
        // a live persistence seam that the HTTP surface consumes — this
        // keeps YAML-applied and API-applied boundaries wire-identical. An
        // absent or all-empty block is a no-op so the unit's default
        // "transparent" view is preserved.
        // Post-#1629 the address is keyed by the unit's actor Guid;
        // result.Unit.Id is the strongly-typed Guid returned by
        // CreateCoreAsync.
        if (manifest.Boundary is { IsEmpty: false })
        {
            await PersistUnitBoundaryAsync(name, result.Unit.Id, manifest.Boundary, cancellationToken);
        }

        // #601 / #603 / #409 B-wide: persist the manifest's `execution:`
        // block through IUnitExecutionStore so the unit's execution
        // defaults match what a PUT /api/v1/units/{id}/execution call
        // would produce. An absent or all-empty block is a no-op so an
        // operator who clears the YAML doesn't re-apply a stale default.
        // ADR-0038 amendment (#2634): forward the manifest's `ai.runtime`
        // into the execution block's `runtime` slot and the structured
        // `ai.model{provider, id}` into the `model` slot — the one
        // canonical execution shape.
        var manifestRuntime = manifest.Ai?.Runtime;
        var manifestModel = ToCatalogModel(manifest.Ai?.Model);
        if (manifest.Execution is { IsEmpty: false }
            || !string.IsNullOrWhiteSpace(manifestRuntime)
            || manifestModel is not null)
        {
            // #1666: IUnitExecutionStore is keyed by the unit's actor Guid
            // (DbUnitExecutionStore parses the id with GuidFormatter.TryParse
            // and throws ArgumentException on a name). Pass the strongly-
            // typed Guid that CreateCoreAsync just minted instead of the
            // user-facing name so the execution block actually lands on the
            // UnitDefinition row — otherwise validation fails with
            // ConfigurationIncomplete: missing image,runtime.
            await PersistUnitExecutionAsync(
                name,
                result.Unit.Id,
                manifest.Execution ?? new ExecutionManifest(),
                manifestRuntime,
                manifestModel,
                cancellationToken);
        }

        // #2204: evaluate the auto-start gate after the execution row is on
        // disk. For package-installed units the manifest's `ai.runtime` /
        // `ai.model` / `execution.image` (incl. package-level inheritance)
        // all flow through, so the gate passes whenever the tenant has a
        // resolvable credential for the runtime's first provider edge.
        var promotedStatus = await TryAutoStartValidationAsync(
            result.Unit.Id, displayName, cancellationToken);
        if (promotedStatus != result.Unit.Status)
        {
            result = result with { Unit = result.Unit with { Status = promotedStatus } };
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
    /// <remarks>
    /// ADR-0038: <paramref name="runtime"/> carries the manifest's
    /// <c>ai.runtime</c> value (the agent-runtime registry id) and
    /// <paramref name="model"/> the structured <c>ai.model{provider, id}</c>
    /// pair. ADR-0039 G8 removed the container-runtime selector from
    /// execution defaults; host configuration owns it.
    /// </remarks>
    private async Task PersistUnitExecutionAsync(
        string unitName,
        Guid unitActorId,
        ExecutionManifest execution,
        string? runtime,
        Cvoya.Spring.Core.Catalog.Model? model,
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
            // ADR-0038 amendment (#2634): the persisted execution block is
            // the one canonical shape — {runtime, model{provider, id},
            // image}. The runtime and structured model come from the `ai:`
            // block; the image from the `execution:` block.
            var defaults = new UnitExecutionDefaults(
                Image: execution.Image,
                Model: model,
                Runtime: runtime);
            // #1666: the store is Guid-keyed — see DbUnitExecutionStore
            // line 80, which throws ArgumentException for a non-Guid id.
            // GuidFormatter.Format is the canonical "N"-format counterpart
            // to the TryParse on the read path.
            var unitId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(unitActorId);
            await _executionStore.SetAsync(unitId, defaults, cancellationToken);
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
                .FirstOrDefaultAsync(u => u.DisplayName == unitId && u.DeletedAt == null, cancellationToken);

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
    /// Writes the manifest <c>instructions:</c> scalar onto the
    /// <see cref="Data.Entities.UnitDefinitionEntity.Definition"/> JSON so the
    /// unit's Instructions slot matches the YAML-authored value. Idempotent:
    /// a re-apply replaces the instructions slot without touching sibling
    /// fields (expertise, execution, …).
    /// </summary>
    private async Task PersistUnitDefinitionInstructionsAsync(
        Guid unitActorId,
        string instructions,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();

            var entity = await db.UnitDefinitions
                .FirstOrDefaultAsync(u => u.Id == unitActorId && u.DeletedAt == null, cancellationToken);

            if (entity is null)
            {
                _logger.LogWarning(
                    "Unit '{UnitId}': could not locate UnitDefinition row to persist instructions; unit will have no instructions seed.",
                    unitActorId);
                return;
            }

            var payload = new Dictionary<string, object?> { ["instructions"] = instructions };

            // Preserve sibling fields already on the Definition document.
            if (entity.Definition is { ValueKind: System.Text.Json.JsonValueKind.Object } existing)
            {
                foreach (var prop in existing.EnumerateObject())
                {
                    if (!string.Equals(prop.Name, "instructions", StringComparison.OrdinalIgnoreCase))
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
                "Unit '{UnitId}': failed to persist instructions from manifest; unit will have no instructions seed.",
                unitActorId);
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
        Guid unitActorId,
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

            var address = Address.ForIdentity("unit", unitActorId);
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
        string? provider,
        string? hosting,
        string? role,
        string? specialty,
        bool? enabled,
        AgentExecutionMode? executionMode,
        IReadOnlyList<MemberManifest> members,
        List<string> warnings,
        UnitConnectorBindingRequest? connector,
        IReadOnlyList<SkillBundleReference> skillReferences,
        bool rejectDuplicates,
        UnitParentInfo parentInfo,
        Guid? preMintedActorId,
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
        var resolvedParents = new List<(Guid Id, DirectoryEntry Entry)>(parentInfo.ParentUnitIds.Count);
        foreach (var parentId in parentInfo.ParentUnitIds)
        {
            var parentAddress = Address.ForIdentity("unit", parentId);
            var parentEntry = await _directoryService.ResolveAsync(parentAddress, cancellationToken);
            if (parentEntry is null)
            {
                throw new UnknownParentUnitException(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(parentId));
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
                    throw new UnknownParentUnitException(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(parentId));
                }
            }
            resolvedParents.Add((parentId, parentEntry));
        }

        // Mint the new unit's identity up-front so the bundle validator (and
        // any policy-enforcer it consults) can key off the unit's stable Guid
        // rather than its display name. Under #1629 the directory is Guid-keyed.
        // PR7: when the package-install pipeline pre-mints the Guid (so the
        // staging row and directory entry agree on a single identity), use
        // that Guid instead of generating a fresh one here.
        var actorGuid = preMintedActorId ?? Guid.NewGuid();

        // Resolve skill bundles and validate their tool requirements up-front
        // as well. Any failure here surfaces to the caller as a typed
        // exception that the endpoint layer maps to a ProblemDetails 4xx so
        // the manifest author sees the exact bundle / tool that rejected
        // creation before we write any state.
        IReadOnlyList<SkillBundle> resolvedBundles = Array.Empty<SkillBundle>();
        if (skillReferences.Count > 0)
        {
            resolvedBundles = await ResolveSkillBundlesAsync(skillReferences, cancellationToken);
            var report = await _bundleValidator.ValidateAsync(actorGuid, resolvedBundles, cancellationToken);
            // Non-blocking warnings (e.g. bundles declaring tools no connector
            // surfaces) ride through the creation response's existing warnings
            // list so the wizard / CLI can surface them alongside manifest-
            // section warnings. Blocking problems throw from ValidateAsync.
            if (report.Warnings.Count > 0)
            {
                warnings.AddRange(report.Warnings);
            }
        }

        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(actorGuid);
        var address = Address.ForIdentity(Address.UnitScheme, actorGuid);

        // #325: when the caller supplies a canonical name override through
        // the request body we reject duplicates up front with a typed
        // exception the endpoint layer maps to 400. Under #1629 the directory
        // is keyed by Guid identity, so collision is checked by display name
        // against persisted unit definitions rather than a directory resolve.
        if (rejectDuplicates)
        {
            await using var dupScope = _scopeFactory.CreateAsyncScope();
            var dupDb = dupScope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var nameTaken = await dupDb.UnitDefinitions
                .AnyAsync(
                    u => u.DisplayName == displayName && u.DeletedAt == null,
                    cancellationToken);
            if (nameTaken)
            {
                throw new DuplicateUnitNameException(displayName);
            }
        }

        var entry = new DirectoryEntry(
            address,
            actorGuid,
            displayName,
            description,
            role,
            DateTimeOffset.UtcNow);

        await _directoryService.RegisterAsync(entry, cancellationToken);

        // #2052 / ADR-0040: top-level units are expressed as an explicit
        // unit_subunit_memberships row whose parent is the tenant id.
        // The edge is the only signal — there is no separate IsTopLevel
        // flag, no zero-edge heuristic. Failure here is non-fatal: the
        // unit is reachable via its directory entry, and the operator
        // can re-parent later by calling AddMemberAsync on a parent
        // unit (which retires the tenant-root edge automatically).
        if (parentInfo.IsTopLevel)
        {
            try
            {
                await _memberGraphStore.EnsureTopLevelEdgeAsync(
                    actorGuid, _tenantContext.CurrentTenantId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "Unit '{UnitName}': failed to write tenant-root edge for top-level unit; the unit will not be visible as a top-level node until the edge is repaired.",
                    name);
            }
        }

        try
        {
            // DisplayName/Description live on the directory entity; only forward
            // the actor-owned fields (Model, Color, …) to the metadata write to
            // avoid a double-write — mirrors UnitEndpoints.CreateUnitAsync.
            // #1732: Tool was dropped from the unit-actor metadata — derived
            // from execution.runtime via the runtime registry at dispatch time.
            // #2341: Specialty / Enabled / ExecutionMode reach the actor through
            // the same SetMetadataAsync write so the manifest authoring surface
            // and PATCH /api/v1/units/{id} agree on the same set of slots.
            var metadata = new UnitMetadata(
                DisplayName: null,
                Description: null,
                Model: model,
                Color: color,
                Provider: provider,
                Hosting: hosting,
                Specialty: specialty,
                Enabled: enabled,
                ExecutionMode: executionMode);

            var proxy = _actorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(actorId), nameof(UnitActor));

            if (metadata.Model is not null || metadata.Color is not null
                || metadata.Provider is not null
                || metadata.Hosting is not null
                || metadata.Specialty is not null
                || metadata.Enabled is not null
                || metadata.ExecutionMode is not null)
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
            //
            // #2768: when the caller is a tenant-user (OSS operator, or a
            // cloud-overlay tenant user), the unit_human_permissions row
            // carries no information — the OSS PermissionService short-
            // circuits tenant-user to implicit Owner, and the cloud overlay
            // keys authorisation off its own per-tenant-user model. Skip the
            // grant in that case rather than auto-minting a phantom Human
            // row to back it.
            var creator = await _callerAccessor.GetCallerAddressAsync(cancellationToken);
            if (creator is { Scheme: Address.HumanScheme })
            {
                var creatorEntry = new UnitPermissionEntry(creator.Id.ToString(), PermissionLevel.Owner);
                await proxy.SetHumanPermissionAsync(creator.Id, creatorEntry, cancellationToken);
            }

            // #2044 / ADR-0040: the human-side mirror onto
            // HumanActor.SetPermissionForUnitAsync is gone. The
            // unit_human_permissions row written above is the single source
            // of truth — there is no second view to keep in sync.

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
                        new ActorId(Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(parentEntry.ActorId)), nameof(UnitActor));
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
            // Cache the directory listing once per call so the resolve-by-name
            // path below stays O(1) per member.
            IReadOnlyList<DirectoryEntry>? memberDirectoryEntries = null;
            foreach (var member in members)
            {
                var resolved = ResolveMemberAddress(member);
                if (resolved is null)
                {
                    warnings.Add("member entry had no 'agent' or 'unit' field; skipped");
                    continue;
                }

                // Manifests carry the member's display name, not its Guid.
                // Resolve through the directory; mint a fresh Guid for
                // auto-register paths (the agent-scheme branch below registers
                // the new entry with this Guid as the actor identity).
                Guid memberGuid;
                if (Cvoya.Spring.Core.Identifiers.GuidFormatter.TryParse(resolved.Value.Path, out var parsedGuid))
                {
                    memberGuid = parsedGuid;
                }
                else
                {
                    memberDirectoryEntries ??= await _directoryService.ListAllAsync(cancellationToken);
                    var match = memberDirectoryEntries.FirstOrDefault(
                        e => string.Equals(e.Address.Scheme, resolved.Value.Scheme, StringComparison.OrdinalIgnoreCase)
                             && string.Equals(e.DisplayName, resolved.Value.Path, StringComparison.Ordinal));
                    memberGuid = match?.ActorId ?? Guid.NewGuid();
                }

                var memberAddress = Address.ForIdentity(resolved.Value.Scheme, memberGuid);

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

                if (string.Equals(resolved.Value.Scheme, "agent", StringComparison.OrdinalIgnoreCase))
                {
                    // Fix #374: auto-register agent-scheme members in the
                    // directory so they are discoverable via GET /api/v1/agents
                    // and the dashboard's Agents section. Idempotent — if the
                    // agent was already registered (e.g. via `spring agent
                    // create` before being added to the unit), the existing
                    // entry is preserved.
                    //
                    // Post-#1629 every Address is keyed by Guid identity. The
                    // memberAddress minted above already carries the agent's
                    // stable Guid (matched to an existing display-name row when
                    // possible, freshly minted otherwise) so we register
                    // against that — never against Address.For(slug), which
                    // would throw on the non-Guid manifest path.
                    try
                    {
                        var existing = await _directoryService.ResolveAsync(memberAddress, cancellationToken);
                        if (existing is null)
                        {
                            var agentEntry = new DirectoryEntry(
                                memberAddress,
                                memberAddress.Id,
                                resolved.Value.Path,  // displayName = member name (slug-form preserved)
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

                    // #2072: the membership row was already written by
                    // proxy.AddMemberAsync above. UnitActor.AddMemberAsync
                    // routes through UnitMembershipCoordinator, which
                    // idempotently writes unit_memberships via
                    // IUnitMemberGraphStore — the canonical
                    // membership-write surface post-#2052. The previous
                    // direct-repository upsert here was a redundant second
                    // write to the same EF row (the stale "Mirror the add
                    // into the DB so …" comment described an actor-state /
                    // EF dual-storage that no longer exists).
                }
            }

            // Persist the resolved skill bundles so prompt assembly can
            // rehydrate them on every message turn without reparsing the
            // manifest. Writes happen after the directory register so we
            // never leave bundle rows behind an un-discoverable unit.
            //
            // #1748: keyed by the unit's actor Guid for consistency with
            // every other unit-keyed store. The state-store path is opaque
            // (a key prefix), so the only effect of switching from name to
            // actorId is rename-safety and uniformity with the connector /
            // execution stores.
            // The store's SetAsync takes SkillBundleReference values and
            // re-resolves them internally so the persisted record always
            // carries the freshest prompt + required-tools snapshot. The
            // pre-resolution above (resolvedBundles) is for synchronous
            // validation only — its lifetime ends with this request.
            if (skillReferences.Count > 0)
            {
                await _bundleStore.SetAsync(actorId, skillReferences, cancellationToken);
            }

            // #947 / T-05: backend-validated creation. Direct-create
            // callers supply `model`/`provider` on the request body;
            // mirror them onto the unit's execution block so the
            // scheduler can read back a consistent view of what to
            // validate against. The manifest path already writes this
            // through PersistUnitExecutionAsync.
            //
            // ADR-0038 amendment (#2634): the persisted execution block
            // carries the structured `model{provider, id}` pair — both
            // halves must be present to write it. `runtime` is left null
            // here (the direct-create body carries no runtime flag) and
            // partial-update semantics preserve any pre-existing value.
            var directModel = !string.IsNullOrWhiteSpace(model)
                    && !string.IsNullOrWhiteSpace(provider)
                ? new Cvoya.Spring.Core.Catalog.Model(provider!.Trim(), model!.Trim())
                : null;
            if (_executionStore is not null && directModel is not null)
            {
                try
                {
                    // #1666: the store parses the unit id as a Guid (see
                    // DbUnitExecutionStore line 80) — passing `name` here
                    // silently failed the persistence write, leaving every
                    // direct-created unit's `definition->'execution'` NULL
                    // and validation reporting ConfigurationIncomplete.
                    // `actorId` is the GuidFormatter.Format(actorGuid)
                    // value computed earlier in this method.
                    await _executionStore.SetAsync(
                        actorId,
                        new UnitExecutionDefaults(
                            Image: null,
                            Model: directModel,
                            Runtime: null),
                        cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Unit '{UnitName}' failed to persist execution defaults on direct create; validation will not start.",
                        name);
                }
            }

            // The auto-start gate (#2156 / #2204) is deliberately NOT evaluated
            // here. CreateCoreAsync runs before the manifest path's
            // PersistUnitExecutionAsync write, so the unit's execution defaults
            // (image / runtime / model) are not yet on disk when this method
            // returns. Evaluating the gate here would always fail on manifest-
            // installed units. The caller invokes
            // <see cref="TryAutoStartValidationAsync"/> after all execution
            // defaults have been persisted.
            var initialStatus = LifecycleStatus.Draft;

            // Bind the connector *after* the actor is reachable — the store
            // talks to the unit actor, which needs the directory entry in
            // place. A failure here rolls the whole creation back (below)
            // so the user never sees a half-configured unit.
            //
            // #1748: IUnitConnectorConfigStore is keyed by the unit's actor
            // Guid (UnitActorConnectorConfigStore.ResolveProxyAsync calls
            // Address.For("unit", unitId) which throws on a non-Guid id).
            // Pass the canonical no-dash form computed earlier instead of
            // the user-facing name — same shape as the #1666 fix for the
            // execution store.
            if (targetConnector is not null)
            {
                try
                {
                    await _connectorConfigStore.SetAsync(
                        actorId,
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
                metadata.Hosting);

            return new UnitCreationResult(response, warnings, membersAdded);
        }
        catch (UnitCreationBindingException)
        {
            await TryRollbackAsync(address, actorId, name, cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// #2204: evaluates the auto-start gate against the unit's persisted
    /// execution defaults and, when the gate passes, transitions the unit to
    /// <see cref="LifecycleStatus.Validating"/> and marks the
    /// <c>Unit:PendingAutoStart</c> flag so
    /// <see cref="IUnitActor.CompleteValidationAsync"/> drives the unit on
    /// through <c>Stopped → Starting → Running</c> once validation succeeds.
    /// Returns the resulting status (<see cref="LifecycleStatus.Validating"/> on a
    /// successful transition, otherwise <see cref="LifecycleStatus.Draft"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Per ADR-0038 the unit does not carry a flat <c>provider</c> slot — the
    /// provider is intrinsic to <c>ai.model</c>. The gate therefore mirrors the
    /// resolution chain used by
    /// <c>ArtefactValidationWorkflowScheduler.ScheduleAsync</c>: read
    /// <see cref="UnitExecutionDefaults"/> from <see cref="IUnitExecutionStore"/>,
    /// resolve the agent-runtime registry id from
    /// <see cref="UnitExecutionDefaults.Agent"/> (Provider as a last-ditch
    /// fallback for spring-voyage-style runtimes), then look up the
    /// catalogue runtime's first provider edge for the
    /// <c>(providerId, authMethod)</c> pair the launcher actually consumes.
    /// </para>
    /// <para>
    /// Skipped when image, runtime, or model is missing, when the runtime
    /// catalogue does not know the runtime, or when the credential resolver
    /// reports <c>NotFound</c>. Skipping is silent — partial configs land in
    /// <see cref="LifecycleStatus.Draft"/> exactly as before; the operator finishes
    /// configuration and calls <c>/revalidate</c>.
    /// </para>
    /// </remarks>
    /// <inheritdoc />
    public Task<LifecycleStatus> TryAutoStartAsync(
        Guid unitActorGuid,
        string unitName,
        CancellationToken cancellationToken)
        => TryAutoStartValidationAsync(unitActorGuid, unitName, cancellationToken);

    private async Task<LifecycleStatus> TryAutoStartValidationAsync(
        Guid unitActorGuid,
        string unitName,
        CancellationToken cancellationToken)
    {
        // #2374: delegate to the shared IArtefactAutoStartGate when DI has
        // wired it (production). The legacy inline path below stays as a
        // fallback so test fixtures that construct UnitCreationService with
        // mock execution-store / credential-resolver / runtime-catalogue
        // continue to exercise the same precondition checks without having
        // to also mock the gate. Test refactor to use the gate directly is
        // tracked under #2373.
        if (_autoStartGate is not null)
        {
            return await _autoStartGate.TryAutoStartAsync(
                ArtefactKind.Unit, unitActorGuid, unitName, cancellationToken);
        }

        if (_executionStore is null)
        {
            return LifecycleStatus.Draft;
        }

        var actorId = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(unitActorGuid);
        UnitExecutionDefaults? defaults;
        try
        {
            defaults = await _executionStore.GetAsync(actorId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex,
                "Unit '{UnitName}' auto-start gate: failed to read execution defaults; leaving unit in Draft.",
                unitName);
            return LifecycleStatus.Draft;
        }

        if (defaults is null || defaults.IsEmpty
            || string.IsNullOrWhiteSpace(defaults.Image)
            || defaults.Model is null)
        {
            return LifecycleStatus.Draft;
        }

        var runtimeId = defaults.Runtime;
        if (string.IsNullOrWhiteSpace(runtimeId))
        {
            return LifecycleStatus.Draft;
        }

        // Mirror ArtefactValidationWorkflowScheduler.cs: the catalogue runtime's
        // first provider edge carries the auth method the launcher consumes.
        // Without a runtime catalogue (legacy test harness) we cannot derive
        // the provider, so the gate stays closed — the same fail-safe shape
        // the scheduler uses when its catalogue lookup misses.
        if (_runtimeCatalog is null)
        {
            return LifecycleStatus.Draft;
        }

        var catalogRuntime = _runtimeCatalog.GetAgentRuntime(runtimeId);
        if (catalogRuntime is null || catalogRuntime.ModelProviders.Count == 0)
        {
            return LifecycleStatus.Draft;
        }

        var edge = catalogRuntime.ModelProviders[0];
        var providerId = edge.Id;
        var authMethod = edge.AuthMethod;

        // Runtimes that declare no credential (Ollama edge: AuthMethod == null)
        // are auto-startable as soon as image + runtime + model are present —
        // the probe layer skips the credential step.
        if (authMethod is not null && _credentialResolver is not null)
        {
            try
            {
                var resolution = await _credentialResolver.ResolveAsync(
                    providerId: providerId,
                    authMethod: authMethod.Value,
                    agentId: null,
                    unitId: unitActorGuid,
                    cancellationToken);
                if (string.IsNullOrEmpty(resolution.Value))
                {
                    return LifecycleStatus.Draft;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Unit '{UnitName}' auto-start gate: credential resolution threw; leaving unit in Draft.",
                    unitName);
                return LifecycleStatus.Draft;
            }
        }

        // All preconditions met — drive the actor into Validating and arm the
        // post-validation auto-start. The flag is consumed once by
        // CompleteValidationAsync (#2156), so a later manual /revalidate
        // still settles in Stopped.
        try
        {
            var proxy = _actorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(actorId), nameof(UnitActor));
            var transitionResult = await proxy.TransitionAsync(
                LifecycleStatus.Validating, cancellationToken);
            if (transitionResult is { Success: true })
            {
                await proxy.SetPendingAutoStartAsync(cancellationToken);
                return LifecycleStatus.Validating;
            }

            _logger.LogWarning(
                "Unit '{UnitName}' failed to transition to Validating on creation: {Reason}. Staying in Draft.",
                unitName, transitionResult?.RejectionReason ?? "unknown");
            return LifecycleStatus.Draft;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Unit '{UnitName}' transition to Validating threw on creation. Staying in Draft.",
                unitName);
            return LifecycleStatus.Draft;
        }
    }

    /// <summary>
    /// Maps a manifest <see cref="AiModelManifest"/> onto the catalogue
    /// <see cref="Cvoya.Spring.Core.Catalog.Model"/> record. Returns
    /// <c>null</c> unless both <c>provider</c> and <c>id</c> are present.
    /// </summary>
    private static Cvoya.Spring.Core.Catalog.Model? ToCatalogModel(AiModelManifest? model)
        => model is not null
            && !string.IsNullOrWhiteSpace(model.Provider)
            && !string.IsNullOrWhiteSpace(model.Id)
            ? new Cvoya.Spring.Core.Catalog.Model(model.Provider!.Trim(), model.Id!.Trim())
            : null;

    /// <summary>
    /// Best-effort rollback: unregisters the directory entry so the caller's
    /// failed creation leaves nothing behind. We deliberately do NOT touch
    /// the actor — absent a directory entry, its state is unreachable and
    /// will be cleared on the next actor reactivation. Unit-scoped secrets
    /// are not yet provisioned at this point (the connector binding is the
    /// last step), so no additional cleanup is needed.
    /// </summary>
    private async Task TryRollbackAsync(Address address, string actorId, string name, CancellationToken ct)
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
        // #1748: bundles are keyed by actorId, matching the SetAsync write.
        try
        {
            await _bundleStore.DeleteAsync(actorId, ct);
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

    private static (string Scheme, string Path)? ResolveMemberAddress(MemberManifest member)
    {
        // ADR-0043 §5g: inline-body members are expanded into synthesised
        // peer artefacts by PackageManifestParser before the activator
        // rewrites references. By the time this service sees the member,
        // both forms have collapsed to a name — the inline body's `name:`
        // when the member was authored inline, or the bare scalar value
        // when authored as a reference. Reading through AgentName / UnitName
        // keeps this single resolution point honest.
        var agentName = member.AgentName;
        if (!string.IsNullOrWhiteSpace(agentName))
        {
            return ("agent", agentName!);
        }
        var unitName = member.UnitName;
        if (!string.IsNullOrWhiteSpace(unitName))
        {
            return ("unit", unitName!);
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
        IReadOnlyList<Guid>? parentUnitIds,
        bool? isTopLevel)
    {
        var normalisedParents = (parentUnitIds ?? Array.Empty<Guid>())
            .Where(id => id != Guid.Empty)
            .Distinct()
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

}

/// <summary>
/// Validated pair of "parent" inputs for <see cref="IUnitCreationService"/>.
/// Exactly one of the two will carry a positive signal: either
/// <see cref="IsTopLevel"/> is <c>true</c> and <see cref="ParentUnitIds"/>
/// is empty, or <see cref="ParentUnitIds"/> has at least one entry and
/// <see cref="IsTopLevel"/> is <c>false</c>.
/// </summary>
public sealed record UnitParentInfo(bool IsTopLevel, IReadOnlyList<Guid> ParentUnitIds);
