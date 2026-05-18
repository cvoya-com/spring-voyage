// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Lifecycle;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Packages;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Host.Api.Auth;
using Cvoya.Spring.Manifest;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using YamlDotNet.RepresentationModel;

/// <summary>
/// Default <see cref="IPackageArtefactActivator"/> implementation.
/// Delegates to <see cref="IUnitCreationService"/> for unit artefacts,
/// reusing the actor-activation path (directory registration, actor metadata
/// writes, member wiring). For agent artefacts, registers a directory entry
/// directly so an AgentPackage installed without an enclosing unit (the
/// portal's <c>/agents/create</c> path) is fully addressable when the
/// install flips to <c>active</c>.
/// </summary>
/// <remarks>
/// #1664: before forwarding a unit manifest to <see cref="IUnitCreationService"/>
/// this activator rewrites every <c>members[]</c> reference into the
/// canonical Guid form of the resolved peer artefact. Resolution probes
/// the install-batch <see cref="LocalSymbolMap"/> first (the per-package
/// pre-minted Guids), then the tenant directory by display name, and
/// finally throws <see cref="UmbrellaMemberNotFoundException"/>. Without
/// this rewrite the creation service's slow-path lookup compared the
/// member's display name against the unit's display name (different
/// strings in a typical package — the YAML <c>name:</c> is human-readable
/// while the manifest's <c>members:</c> entries use the package slug) and
/// silently minted fresh Guids on miss, leaving the children stranded at
/// top level in the Explorer tree.
/// </remarks>
public class DefaultPackageArtefactActivator : IPackageArtefactActivator
{
    private readonly IUnitCreationService _unitCreationService;
    private readonly IDirectoryService _directoryService;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IActorProxyFactory _actorProxyFactory;
    private readonly IPackageHumanResolutionPolicy _humanResolutionPolicy;
    private readonly IAuthenticatedCallerAccessor _callerAccessor;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<DefaultPackageArtefactActivator> _logger;

    /// <summary>
    /// Initialises a new <see cref="DefaultPackageArtefactActivator"/>.
    /// </summary>
    public DefaultPackageArtefactActivator(
        IUnitCreationService unitCreationService,
        IDirectoryService directoryService,
        IServiceScopeFactory scopeFactory,
        IActorProxyFactory actorProxyFactory,
        IPackageHumanResolutionPolicy humanResolutionPolicy,
        IAuthenticatedCallerAccessor callerAccessor,
        ITenantContext tenantContext,
        ILogger<DefaultPackageArtefactActivator> logger)
    {
        _unitCreationService = unitCreationService;
        _directoryService = directoryService;
        _scopeFactory = scopeFactory;
        _actorProxyFactory = actorProxyFactory;
        _humanResolutionPolicy = humanResolutionPolicy;
        _callerAccessor = callerAccessor;
        _tenantContext = tenantContext;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ActivateAsync(
        string packageName,
        ResolvedArtefact artefact,
        Guid installId,
        LocalSymbolMap symbolMap,
        IReadOnlyDictionary<string, ConnectorBinding>? connectorBindings = null,
        ResolvedExecutionDefaults? executionDefaults = null,
        string? displayNameOverride = null,
        string? inheritedAgentHosting = null,
        CancellationToken cancellationToken = default)
    {
        if (artefact.Content is null)
        {
            // Cross-package artefacts are already active in another installed package;
            // no activation needed.
            return;
        }

        switch (artefact.Kind)
        {
            case ArtefactKind.Unit:
                await ActivateUnitAsync(artefact, symbolMap, connectorBindings, executionDefaults, displayNameOverride, cancellationToken);
                break;

            case ArtefactKind.Agent:
                await ActivateAgentAsync(artefact, symbolMap, displayNameOverride, inheritedAgentHosting, cancellationToken);
                break;

            case ArtefactKind.Skill:
            case ArtefactKind.HumanTemplate:
                // Skills and human templates are registered via other paths
                // that read from disk at resolution time; no actor-
                // activation step. Humans are materialised into HumanEntity
                // rows at install time per-declaration; the template body
                // itself is inert.
                _logger.LogDebug(
                    "Artefact {Kind} '{Name}' in package '{Package}' does not require actor activation.",
                    artefact.Kind, artefact.Name, packageName);
                break;

            default:
                _logger.LogWarning(
                    "Unknown artefact kind {Kind} for '{Name}' in package '{Package}'; skipping activation.",
                    artefact.Kind, artefact.Name, packageName);
                break;
        }
    }

    private async Task ActivateUnitAsync(
        ResolvedArtefact artefact,
        LocalSymbolMap symbolMap,
        IReadOnlyDictionary<string, ConnectorBinding>? connectorBindings,
        ResolvedExecutionDefaults? executionDefaults,
        string? displayNameOverride,
        CancellationToken ct)
    {
        var manifest = ManifestParser.Parse(artefact.Content!);

        // #1679: overlay the resolved execution defaults onto the parsed
        // manifest's execution block before forwarding to the unit
        // creation service. The resolver has already done the field-wise
        // merge (member non-null wins, package fills the gaps); we just
        // project the merged values onto the manifest model the
        // downstream IUnitExecutionStore.SetAsync write reads from.
        // Doing it in the activator (rather than threading another
        // parameter through IUnitCreationService) keeps the creation
        // service's signature unchanged and means the manifest path and
        // the dedicated PUT /api/v1/units/{id}/execution path land on
        // the same persisted shape.
        if (executionDefaults is { IsEmpty: false })
        {
            manifest.Execution ??= new ExecutionManifest();
            if (!string.IsNullOrWhiteSpace(executionDefaults.Image))
            {
                manifest.Execution.Image = executionDefaults.Image;
            }
            // ADR-0038: ExecutionManifest no longer carries `provider`.
            // The package-level inherited Provider is dropped here on the
            // unit-write path; the model id still flows through.
            if (!string.IsNullOrWhiteSpace(executionDefaults.Model))
            {
                manifest.Execution.Model = executionDefaults.Model;
            }
        }

        // #1629 PR7: pull the unit's pre-minted Guid out of the symbol map so
        // the directory entry the creation service writes shares a single
        // identity with the staging row Phase 1 already committed. Without
        // this, RegisterAsync would mint a fresh Guid and the install would
        // produce two near-duplicate UnitDefinitionEntity rows for the same
        // display name — exactly the inconsistency #1629 PR7 sets out to fix.
        var actorId = symbolMap.GetOrMint(ArtefactKind.Unit, artefact.Name);

        // #1664: rewrite each `members:` entry's reference field to the
        // canonical Guid form of the resolved peer artefact. The downstream
        // unit-creation service's member loop has a Guid-vs-name fork: a Guid
        // takes the fast path; a name fall-through hits the directory by
        // display name and silently mints a fresh Guid on miss — which is
        // exactly the bug this fix addresses.
        //
        // Resolution precedence:
        //   1. Symbol map (peer artefacts in this same install batch).
        //      The map was minted in Phase 1 by PackageInstallService so
        //      every in-package unit / agent already has a Guid.
        //   2. Directory by display name (member already exists from a
        //      prior install or a manual create).
        //   3. Throw UmbrellaMemberNotFoundException — installing an
        //      umbrella whose members aren't being created and don't already
        //      exist is operator error, not a silent stranding.
        if (manifest.Members is { Count: > 0 })
        {
            await ResolveMemberReferencesAsync(manifest.Members, symbolMap, ct);
        }

        // #2310: when the install request supplied a display-name override,
        // forward it through UnitCreationOverrides.DisplayName so the
        // directory entry + unit_definitions row carry the operator's
        // chosen label instead of the package's declared `name:`. Identity
        // remains the pre-minted Guid; the override is purely cosmetic.
        var overrides = new UnitCreationOverrides(
            IsTopLevel: true,
            ActorId: actorId,
            DisplayName: string.IsNullOrWhiteSpace(displayNameOverride) ? null : displayNameOverride);

        // #1671: forward the resolved per-unit connector binding to
        // IUnitCreationService through the existing UnitConnectorBindingRequest
        // parameter. v0.1 has at most one connector per unit (only github
        // exists), so we project the first slug. The store contract is
        // single-binding-per-unit; multi-slug-per-unit lands when a second
        // connector type with overlapping unit scope arrives.
        Models.UnitConnectorBindingRequest? bindingRequest = null;
        if (connectorBindings is { Count: > 0 })
        {
            var (slug, binding) = connectorBindings.First();
            bindingRequest = new Models.UnitConnectorBindingRequest(
                TypeId: Guid.Empty,
                TypeSlug: slug,
                Config: binding.Config);
        }

        await _unitCreationService.CreateFromManifestAsync(manifest, overrides, ct, bindingRequest);

        // ADR-0046 §1, §7: package-declared human members live on the
        // unit's `members:` list under the `human:` key prefix. For each
        // entry, ask the resolution policy who fills the position
        // (typically a freshly-minted HumanEntity for the OSS default),
        // then upsert a single (unit, human) membership row carrying the
        // multi-valued roles / expertise / notifications.
        await ResolveAndPersistHumansAsync(manifest, actorId, ct);
    }

    /// <summary>
    /// Walks the unit manifest's <c>- human:</c> member entries and
    /// persists one <see cref="UnitMembershipHumanEntity"/> per resolved
    /// Guid via the registered <see cref="IPackageHumanResolutionPolicy"/>
    /// (ADR-0046 §1, §7). Idempotent on the unique <c>(tenant, unit,
    /// human)</c> index — repeated installs of the same package update
    /// the row's roles / expertise / notifications in place rather than
    /// inserting duplicates.
    /// </summary>
    /// <exception cref="PackageHumanResolutionException">
    /// Thrown when the policy returns
    /// <see cref="PackageHumanResolutionOutcome.Rejected"/>. Surfaces
    /// through <c>PackageInstallService</c>'s Phase-2 failure path.
    /// </exception>
    private async Task ResolveAndPersistHumansAsync(
        UnitManifest manifest,
        Guid unitId,
        CancellationToken ct)
    {
        if (manifest.Members is not { Count: > 0 })
        {
            return;
        }

        var humanEntries = new List<HumanManifest>();
        foreach (var member in manifest.Members)
        {
            if (member.Human?.InlineBody is null)
            {
                continue;
            }
            var human = ParseHumanInlineBody(member.Human.InlineBody);
            if (human is null)
            {
                continue;
            }
            humanEntries.Add(human);
        }

        if (humanEntries.Count == 0)
        {
            return;
        }

        // Resolve the install caller once per activation. The policy
        // receives it on every request so it can work from worker /
        // out-of-request install paths too — the accessor returns null
        // when no authenticated principal is present (#2405).
        var callerAddress = await _callerAccessor.GetCallerAddressAsync(ct);
        var callerHumanId = callerAddress is { Scheme: "human" } && callerAddress.Id != Guid.Empty
            ? callerAddress.Id
            : (Guid?)null;

        var tenantId = _tenantContext.CurrentTenantId;
        var unitDisplayName = !string.IsNullOrWhiteSpace(manifest.DisplayName)
            ? manifest.DisplayName!
            : manifest.Name ?? string.Empty;

        await using var scope = _scopeFactory.CreateAsyncScope();
        var membershipStore = scope.ServiceProvider
            .GetRequiredService<Cvoya.Spring.Core.Units.IUnitHumanMembershipStore>();

        for (var i = 0; i < humanEntries.Count; i++)
        {
            var human = humanEntries[i];
            var roles = NormaliseStringList(human.Roles);
            var expertise = NormaliseStringList(human.Expertise);
            var notifications = NormaliseStringList(human.Notifications);

            var request = new PackageHumanResolutionRequest(
                TenantId: tenantId,
                UnitId: unitId,
                UnitDisplayName: unitDisplayName,
                Roles: roles,
                Expertise: expertise,
                Notifications: notifications,
                DisplayName: human.DisplayName,
                Description: human.Description,
                InstallCallerHumanId: callerHumanId);

            var resolution = await _humanResolutionPolicy.ResolveAsync(request, ct);

            switch (resolution.Outcome)
            {
                case PackageHumanResolutionOutcome.Rejected:
                    throw new PackageHumanResolutionException(
                        string.Join(",", roles), unitDisplayName, resolution.Reason);

                case PackageHumanResolutionOutcome.Skipped:
                    _logger.LogInformation(
                        "Package members[human {Index}] (roles=[{Roles}]) on unit {UnitId} skipped: {Reason}.",
                        i, string.Join(", ", roles), unitId,
                        resolution.Reason ?? "(no reason supplied)");
                    continue;

                case PackageHumanResolutionOutcome.Resolved:
                    if (resolution.HumanIds is not { Count: > 0 })
                    {
                        _logger.LogWarning(
                            "Package members[human {Index}] (roles=[{Roles}]) on unit {UnitId}: policy " +
                            "returned Resolved with no Guids; treating as Skipped.",
                            i, string.Join(", ", roles), unitId);
                        continue;
                    }

                    foreach (var humanId in resolution.HumanIds.Distinct())
                    {
                        await membershipStore.UpsertAsync(
                            unitId, humanId, roles, expertise, notifications, ct);
                    }
                    break;

                default:
                    _logger.LogWarning(
                        "Package members[human {Index}] on unit {UnitId}: policy returned unknown " +
                        "outcome {Outcome}; skipping.",
                        i, unitId, resolution.Outcome);
                    continue;
            }
        }
    }

    /// <summary>
    /// Parses a captured inline <c>human:</c> body into the typed
    /// <see cref="HumanManifest"/> shape the resolution loop consumes.
    /// Returns <see langword="null"/> for malformed bodies so the install
    /// pipeline does not abort on a single bad entry — the parser already
    /// rejects most shape errors at upload, and a body that survives parsing
    /// but fails the typed deserializer here logs and skips.
    /// </summary>
    private HumanManifest? ParseHumanInlineBody(string inlineBody)
    {
        try
        {
            var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
                .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            return deserializer.Deserialize<HumanManifest>(inlineBody);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to parse `- human:` inline body; skipping the entry. Body: {Body}",
                inlineBody);
            return null;
        }
    }

    private static List<string> NormaliseStringList(IEnumerable<string>? raw)
        => (raw ?? Array.Empty<string>())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .ToList();

    /// <summary>
    /// Rewrites every <see cref="MemberManifest"/> reference (<c>unit:</c> /
    /// <c>agent:</c>) in place from a local symbol or display name into the
    /// canonical <c>"N"</c>-format Guid of the resolved target. References
    /// that already parse as Guids are left untouched (they are the
    /// cross-package wire form and the creation service already takes the
    /// Guid fast path on them).
    /// </summary>
    /// <exception cref="UmbrellaMemberNotFoundException">
    /// Thrown when a non-Guid reference resolves neither through the
    /// install-batch symbol map nor through a directory display-name lookup.
    /// Surfaced through <see cref="PackageInstallService"/>'s Phase-2
    /// failure handling so the operator sees a precise message rather than
    /// the install silently leaving members stranded at top level.
    /// </exception>
    private async Task ResolveMemberReferencesAsync(
        IReadOnlyList<MemberManifest> members,
        LocalSymbolMap symbolMap,
        CancellationToken ct)
    {
        // Cache the directory listing once per call — both the unit-typed and
        // agent-typed branches consult the same snapshot. Loaded lazily so
        // members fully resolvable through the symbol map alone don't pay
        // for a directory round-trip.
        IReadOnlyList<DirectoryEntry>? directoryEntries = null;

        foreach (var member in members)
        {
            // ADR-0043 §5g: inline bodies in the original YAML have already
            // been synthesised into peer artefacts by PackageManifestParser,
            // so by this point an inline member's `.Agent` / `.Unit` slot
            // collapses through `AgentName` / `UnitName` to the body's
            // `name:` field — exactly the local symbol the symbol map keys
            // off. Rewriting the slot back to a bare scalar reference keeps
            // the downstream unit-creation service's symbol → Guid fork
            // simple.
            var unitName = member.UnitName;
            if (!string.IsNullOrWhiteSpace(unitName))
            {
                // Unit-typed members are the heart of #1664. An unresolvable
                // unit reference would mint a phantom Guid that has no
                // corresponding unit_definitions row — the failure mode is
                // a "stranded" sub-unit hierarchy in the Explorer tree.
                // Fail loudly here so the operator hears about it.
                string resolved;
                (resolved, directoryEntries) = await ResolveUnitReferenceAsync(
                    unitName!, symbolMap, directoryEntries, ct);
                member.Unit = InlineArtefactDefinition.FromReference(resolved);
                continue;
            }

            var agentName = member.AgentName;
            if (!string.IsNullOrWhiteSpace(agentName))
            {
                // Agent-typed members keep the historic auto-register fall-
                // back: when the agent isn't a batch peer or a pre-existing
                // directory entry, the reference rides through unchanged
                // and the unit-creation service's agent-scheme branch
                // mints a Guid and registers it. Pre-#1664 the OSS package
                // (and any package that lists agents only inside sub-unit
                // YAMLs rather than at package level) relied on exactly
                // this fallback, so tightening it here would be a separate,
                // larger refactor.
                string resolved;
                (resolved, directoryEntries) = await ResolveAgentReferenceAsync(
                    agentName!, symbolMap, directoryEntries, ct);
                member.Agent = InlineArtefactDefinition.FromReference(resolved);
            }
            // Members with neither field set fall through to the creation
            // service, which surfaces the same "no 'agent' or 'unit' field"
            // warning it always has.
        }
    }

    private async Task<(string Resolved, IReadOnlyList<DirectoryEntry>? Snapshot)> ResolveUnitReferenceAsync(
        string reference,
        LocalSymbolMap symbolMap,
        IReadOnlyList<DirectoryEntry>? directoryEntries,
        CancellationToken ct)
    {
        // 1. Symbol map first. Also handles the cross-package Guid form —
        //    LocalSymbolMap.TryResolve probes GuidFormatter.TryParse before
        //    the dictionary so a 32-char no-dash hex value parses as a Guid
        //    and the reference rides through unchanged.
        if (symbolMap.TryResolve(ArtefactKind.Unit, reference, out var guid))
        {
            return (GuidFormatter.Format(guid), directoryEntries);
        }

        // 2. Directory fall-back by display name. Lets a manifest reference
        //    a unit created by an earlier install or by direct create — i.e.
        //    a member that's already in the directory but isn't a peer in
        //    this batch.
        directoryEntries ??= await _directoryService.ListAllAsync(ct);
        var match = directoryEntries.FirstOrDefault(e =>
            string.Equals(e.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.DisplayName, reference, StringComparison.Ordinal));
        if (match is not null)
        {
            return (GuidFormatter.Format(match.ActorId), directoryEntries);
        }

        // 3. Fail loudly. Operator error: the umbrella names a unit
        //    member that's neither in the install batch nor in the tenant
        //    directory. Pre-#1664 this silently minted a fresh Guid and
        //    left the member orphaned at the top of the Explorer tree.
        throw new UmbrellaMemberNotFoundException(reference, "unit");
    }

    private async Task<(string Resolved, IReadOnlyList<DirectoryEntry>? Snapshot)> ResolveAgentReferenceAsync(
        string reference,
        LocalSymbolMap symbolMap,
        IReadOnlyList<DirectoryEntry>? directoryEntries,
        CancellationToken ct)
    {
        // 1. Symbol map.
        if (symbolMap.TryResolve(ArtefactKind.Agent, reference, out var guid))
        {
            return (GuidFormatter.Format(guid), directoryEntries);
        }

        // 2. Directory fall-back by display name.
        directoryEntries ??= await _directoryService.ListAllAsync(ct);
        var match = directoryEntries.FirstOrDefault(e =>
            string.Equals(e.Address.Scheme, "agent", StringComparison.OrdinalIgnoreCase)
            && string.Equals(e.DisplayName, reference, StringComparison.Ordinal));
        if (match is not null)
        {
            return (GuidFormatter.Format(match.ActorId), directoryEntries);
        }

        // 3. Pass-through. The downstream unit-creation service auto-
        //    registers the agent with a fresh Guid and writes a directory
        //    entry — that is the historic OSS-package install path for
        //    agents that only appear inside sub-unit `members:` lists.
        //    Tightening this to a hard failure is out of scope for #1664.
        return (reference, directoryEntries);
    }

    /// <summary>
    /// Activates a standalone agent artefact by registering it in the
    /// directory and persisting any execution / ai blocks onto the
    /// <see cref="AgentDefinitionEntity.Definition"/> JSON column. Pre-#1559
    /// this was a no-op, which silently broke the portal's
    /// <c>/agents/create</c> flow: the install pipeline reported
    /// <c>active</c> but no directory entry existed, so the subsequent
    /// <c>POST /api/v1/tenant/units/{unit}/agents/{agent}</c> membership
    /// call returned 404 ("Agent not found").
    /// </summary>
    /// <remarks>
    /// Idempotent at the directory level — <see cref="IDirectoryService.RegisterAsync"/>
    /// upserts on the agent path, so repeated activations of the same agent
    /// are safe. The execution/ai block is best-effort: any parse failure
    /// is logged as a warning and registration proceeds without it (the
    /// platform's runtime catalog falls back to defaults).
    /// </remarks>
    private async Task ActivateAgentAsync(
        ResolvedArtefact artefact,
        LocalSymbolMap symbolMap,
        string? displayNameOverride,
        string? inheritedAgentHosting,
        CancellationToken ct)
    {
        var content = artefact.Content!;
        AgentManifestFields fields;
        try
        {
            fields = ParseAgentManifest(content, inheritedAgentHosting);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Agent artefact '{Name}': failed to parse YAML; activation skipped.",
                artefact.Name);
            throw;
        }

        // Prefer the explicit `name` (slug) under `agent:` if supplied;
        // fall back to the resolved artefact name. The slug is what
        // becomes the address path.
        var slug = string.IsNullOrWhiteSpace(fields.Id) ? artefact.Name : fields.Id!;
        if (string.IsNullOrWhiteSpace(slug))
        {
            // #2156: pre-fix this was a warning-and-return. Treat as a hard
            // install failure so the caller sees a precise diagnostic — an
            // anonymous agent silently dropping out of the install batch is
            // exactly the operator-confusing failure mode the issue calls out.
            throw new InvalidOperationException(
                $"Agent artefact '{artefact.Name}' has no name; cannot register in directory.");
        }

        // #2310: the install pipeline may have supplied a display-name
        // override for the package's single top-level activatable. When
        // present, it wins over both the agent YAML's `name:` field
        // (i.e. fields.DisplayName) and the slug. Used so the operator
        // can install the same agent package multiple times without
        // every instance landing under the same display name.
        var displayName = !string.IsNullOrWhiteSpace(displayNameOverride)
            ? displayNameOverride!
            : (string.IsNullOrWhiteSpace(fields.DisplayName) ? slug : fields.DisplayName!);
        var description = fields.Description ?? string.Empty;

        // #1629 PR7: identity is server-allocated; the local-symbol map is
        // the source of truth for (kind, name) → Guid so a retry reuses the
        // pre-allocated Guid. Mint first, build the address from the Guid
        // (ADR-0036 wire form is `scheme:<32-hex>` — never a slug), then
        // probe the directory in case a prior Phase-2 partial-failure left
        // the registration in place.
        var actorId = symbolMap.GetOrMint(ArtefactKind.Agent, artefact.Name);
        var address = Address.ForIdentity("agent", actorId);
        var existing = await _directoryService.ResolveAsync(address, ct);
        actorId = existing?.ActorId ?? actorId;

        var entry = new DirectoryEntry(
            address,
            actorId,
            displayName,
            description,
            fields.Role,
            DateTimeOffset.UtcNow);

        // Build an actor proxy up-front so failures further down can flip
        // the agent's lifecycle row to Error before rethrowing — without
        // this the install would silently fail and the operator would have
        // to comb worker logs to understand why an agent never picked up
        // messages (#2156).
        var actorProxy = _actorProxyFactory.CreateActorProxy<IAgentActor>(
            new ActorId(GuidFormatter.Format(actorId)), nameof(AgentActor));

        try
        {
            await _directoryService.RegisterAsync(entry, ct);
        }
        catch (Exception ex)
        {
            // Surface the failure on the agent's lifecycle row so the GET
            // endpoint can show "why" without forcing the operator to
            // diagnose by log inspection. Best-effort: a state write that
            // also throws falls through to the rethrow below.
            await TrySetLifecycleErrorAsync(actorProxy, slug, ex, ct);
            throw;
        }

        // Persist the execution / ai block as the AgentDefinitionEntity's
        // Definition JSON so IAgentDefinitionProvider can surface the
        // execution configuration to the dispatcher (same shape the CLI's
        // `spring agent create --definition` writes).
        if (fields.DefinitionJson is { } defJson)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
                var entity = await db.AgentDefinitions
                    .FirstOrDefaultAsync(a => a.Id == actorId, ct);
                if (entity is not null)
                {
                    entity.Definition = defJson;
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (Exception ex)
            {
                // #2156: pre-fix this was a warning-and-continue, which
                // left the package reporting "active" while the agent's
                // execution block was missing — a silent partial install.
                // Flip the lifecycle row to Error so the operator sees the
                // failure, then rethrow so the install pipeline surfaces it.
                await TrySetLifecycleErrorAsync(actorProxy, slug, ex, ct);
                throw;
            }
        }

        // #2364: drive the agent through the auto-start gate — schedules
        // the shared ArtefactValidationWorkflow and arms the post-validation
        // auto-start so the agent reaches Running without an operator click.
        // Mirrors UnitCreationService.TryAutoStartValidationAsync; agents
        // skip the connector-dispatcher hop (no per-agent connector bindings
        // in v0.1).
        await TryAutoStartAgentAsync(actorId, slug, ct);

        _logger.LogInformation(
            "Agent artefact '{Name}' registered (actorId={ActorId}, role={Role}).",
            slug, actorId, fields.Role ?? "(none)");
    }

    /// <summary>
    /// Best-effort flip of the agent's lifecycle row to
    /// <see cref="LifecycleStatus.Error"/>. Called from the
    /// <c>catch</c> blocks in <see cref="ActivateAgentAsync"/>; a failure
    /// inside this method is swallowed so the original exception is the
    /// one that surfaces to the install pipeline.
    /// </summary>
    /// <summary>
    /// Drives a freshly-installed agent through the shared
    /// <see cref="IArtefactAutoStartGate"/> (#2374). When DI has wired the
    /// gate, this becomes a tiny shim; the previous inline implementation
    /// lived here and on <c>UnitCreationService</c> — both now delegate to
    /// the same service so the direct-create endpoint (<c>CreateAgentAsync</c>)
    /// can use the same logic without duplication.
    /// </summary>
    private async Task TryAutoStartAgentAsync(
        Guid agentActorGuid,
        string agentName,
        CancellationToken ct)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var gate = scope.ServiceProvider.GetService<IArtefactAutoStartGate>();
        if (gate is null)
        {
            // Legacy / test-harness fallback: no gate wired — leave the
            // agent in Draft. Operators can still revalidate.
            return;
        }

        _ = await gate.TryAutoStartAsync(ArtefactKind.Agent, agentActorGuid, agentName, ct);
    }

    private Task TrySetLifecycleErrorAsync(
        IAgentActor actorProxy,
        string slug,
        Exception ex,
        CancellationToken ct)
    {
        // #2364: the old AgentLifecycleStatus.Error sentinel is gone.
        // The new state machine has no Draft → Error edge (an agent
        // that fails activation never entered Validating), so we just
        // log the install failure here. The activator's caller rethrows
        // the original exception so the install pipeline still surfaces
        // it to the operator. Phase 4 may add a richer error path once
        // the auto-start gate is wired.
        _logger.LogWarning(
            ex,
            "Agent artefact '{Name}': activation failed; agent left unregistered. Operator-visible message: {Message}",
            slug, ex.Message);
        _ = actorProxy; // parameter retained for future Phase 4 wiring
        _ = ct;
        return Task.CompletedTask;
    }

    /// <summary>Minimal projection of the agent YAML fields the activator needs.</summary>
    private sealed record AgentManifestFields(
        string? Id,
        string? DisplayName,
        string? Role,
        string? Description,
        JsonElement? DefinitionJson);

    /// <summary>
    /// Parses an agent YAML block (the body of <c>agent:</c> in an
    /// AgentPackage manifest) into the fields we care about for directory
    /// registration. The execution / ai block is round-tripped through
    /// JSON so it can land verbatim in <see cref="AgentDefinitionEntity.Definition"/>.
    /// </summary>
    /// <remarks>
    /// Issue #2436: when <paramref name="inheritedAgentHosting"/> is
    /// non-null and the agent's own <c>execution:</c> block does not
    /// declare a <c>hosting:</c> literal, the inherited value (resolved
    /// from the agent's containing unit and the agent template chain by
    /// the install pipeline) lands on the persisted definition JSON's
    /// <c>execution.hosting</c> slot. Precedence: agent's own
    /// <c>execution.hosting</c> &gt; inherited value &gt; absent (the
    /// dispatcher's <c>persistent</c> default).
    /// </remarks>
    private static AgentManifestFields ParseAgentManifest(
        string yamlText,
        string? inheritedAgentHosting = null)
    {
        // The Content is the FULL package YAML (matching what
        // PackageManifestParser passes to ResolvedArtefact.Content);
        // walk down to either the top-level `agent:` mapping or to a
        // top-level mapping with `id`/`name` fields if the body was
        // resolved to the agent block directly.
        var stream = new YamlStream();
        using (var reader = new System.IO.StringReader(yamlText))
        {
            stream.Load(reader);
        }

        if (stream.Documents.Count == 0)
        {
            return new AgentManifestFields(null, null, null, null, null);
        }

        var root = stream.Documents[0].RootNode as YamlMappingNode
            ?? throw new InvalidOperationException("Agent manifest root is not a YAML mapping.");

        // Prefer the `agent:` block; fall back to the root if the artefact
        // body was extracted to just the agent mapping.
        YamlMappingNode? agentNode = null;
        if (root.Children.TryGetValue(new YamlScalarNode("agent"), out var raw)
            && raw is YamlMappingNode agentMap)
        {
            agentNode = agentMap;
        }
        else if (root.Children.ContainsKey(new YamlScalarNode("id")) ||
                 root.Children.ContainsKey(new YamlScalarNode("name")))
        {
            agentNode = root;
        }

        if (agentNode is null)
        {
            return new AgentManifestFields(null, null, null, null, null);
        }

        var id = ScalarValue(agentNode, "id");
        // ADR-0043 §5d: `displayName:` is the declarative human-readable
        // label; it sits at the top level alongside `name:` (the slug /
        // address path). Honour it when present so packages that ship
        // friendly labels for nested agents land with the right
        // DisplayName on the directory entry. The historical fallback
        // (use `name:` as the display label) stays in place via the
        // coalesce below so YAMLs that omit `displayName:` keep their
        // pre-change behaviour byte-for-byte.
        var declaredDisplayName = ScalarValue(agentNode, "displayName");
        var declaredName = ScalarValue(agentNode, "name");
        var displayName = !string.IsNullOrWhiteSpace(declaredDisplayName)
            ? declaredDisplayName
            : declaredName;
        var role = ScalarValue(agentNode, "role");
        var description = ScalarValue(agentNode, "description");

        // Build the Definition JSON from execution + ai blocks if present.
        // The AgentDefinitionEntity.Definition shape is the same JSON the
        // CLI's `--definition` flag accepts.
        var defObj = new Dictionary<string, object?>();

        // Capture the YAML's two declarative shapes:
        //   • `execution:` — modern wire form, mirrors what
        //     IAgentExecutionStore.SetAsync writes (image/model/agent/...).
        //   • `ai:` — ADR-0038 authoring shape with nested `runtime`,
        //     `model{provider,id}`, and `environment.image`.
        // The dispatcher's IAgentDefinitionProvider already reads both shapes
        // (DbAgentDefinitionProvider.ExtractExecution), but the auto-start
        // gate (#2364 / #2374) reads through IAgentExecutionStore which only
        // recognises the modern `execution:` block. Without the projection
        // below, agents authored with the ADR-0038 shape land in Draft after
        // install because the gate cannot read image/model/runtime out of
        // `ai.environment.image` + `ai.runtime` + `ai.model.id`.
        Dictionary<string, object?>? execBlock = null;
        if (agentNode.Children.TryGetValue(new YamlScalarNode("execution"), out var execRaw)
            && execRaw is YamlMappingNode execMap)
        {
            execBlock = ToObject(execMap) as Dictionary<string, object?>;
        }

        Dictionary<string, object?>? aiBlock = null;
        if (agentNode.Children.TryGetValue(new YamlScalarNode("ai"), out var aiRaw)
            && aiRaw is YamlMappingNode aiMap)
        {
            aiBlock = ToObject(aiMap) as Dictionary<string, object?>;
            defObj["ai"] = aiBlock;
        }

        // Project (ai, execution) onto the canonical `execution:` block the
        // gate + dispatcher read. Existing top-level execution slots win
        // (the YAML author explicitly set them); missing slots are filled
        // from the structured `ai:` block per the same mapping
        // UnitCreationService.PersistUnitExecutionAsync uses for units.
        //
        // Mapping (closes #2388):
        //   ai.runtime           → execution.agent     (runtime registry id)
        //   ai.model.provider    → execution.provider  (LLM provider id)
        //   ai.model.id          → execution.model     (provider-scoped model id)
        //   ai.environment.image → execution.image     (only if execution.image absent)
        var projected = ProjectExecutionBlock(execBlock, aiBlock);

        // Issue #2436: layer the install-time inherited hosting onto the
        // projected execution block only when the agent's own YAML did
        // not declare `execution.hosting`. The agent-level value (already
        // captured into `projected["hosting"]` from `execBlock`) wins
        // outright per the precedence chain agent > template > unit >
        // default(`persistent`). Templates are not represented here
        // explicitly — `inheritedAgentHosting` is the literal the install
        // pipeline resolved across that chain.
        if (!string.IsNullOrWhiteSpace(inheritedAgentHosting)
            && !projected.ContainsKey("hosting"))
        {
            projected["hosting"] = inheritedAgentHosting!;
        }

        if (projected is { Count: > 0 })
        {
            defObj["execution"] = projected;
        }

        JsonElement? defJson = null;
        if (defObj.Count > 0)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(defObj));
            defJson = doc.RootElement.Clone();
        }

        return new AgentManifestFields(id, displayName, role, description, defJson);
    }

    /// <summary>
    /// Projects an agent YAML's <c>execution:</c> + <c>ai:</c> blocks onto the
    /// canonical <c>execution: { image, agent, provider, model, hosting }</c>
    /// shape consumed by <see cref="Cvoya.Spring.Core.Execution.IAgentExecutionStore"/>
    /// and the auto-start gate. Existing slots on <paramref name="execBlock"/>
    /// always win — the projection only fills gaps from the structured
    /// <c>ai:</c> block. Returns an empty dictionary when neither input
    /// contributes any field (leaves the Definition's <c>execution</c> key
    /// unwritten — same shape as before the projection).
    /// </summary>
    /// <remarks>
    /// Mirrors the field mapping
    /// <see cref="UnitCreationService.PersistUnitExecutionAsync"/> uses to
    /// land manifest fields on <see cref="Cvoya.Spring.Core.Execution.IUnitExecutionStore"/>:
    /// <c>ai.runtime → Agent</c>, <c>ai.model.id → Model</c>,
    /// <c>ai.model.provider → Provider</c>, manifest <c>execution.image →
    /// Image</c>. The agent-side equivalent has to live in the activator
    /// because the agent path writes <c>Definition</c> directly rather than
    /// going through the store.
    /// </remarks>
    private static Dictionary<string, object?> ProjectExecutionBlock(
        Dictionary<string, object?>? execBlock,
        Dictionary<string, object?>? aiBlock)
    {
        var result = new Dictionary<string, object?>();

        // Start from whatever the YAML's top-level `execution:` already
        // declares — operator-supplied values are authoritative.
        if (execBlock is not null)
        {
            foreach (var kvp in execBlock)
            {
                if (kvp.Value is string s && !string.IsNullOrWhiteSpace(s))
                {
                    result[kvp.Key] = s.Trim();
                }
            }
        }

        if (aiBlock is null)
        {
            return result;
        }

        // ai.runtime → execution.agent (the runtime registry id; gate reads
        // this slot as defaults.Agent in ArtefactAutoStartGate).
        if (!result.ContainsKey("agent") && aiBlock.TryGetValue("runtime", out var rt) && rt is string runtime && !string.IsNullOrWhiteSpace(runtime))
        {
            result["agent"] = runtime.Trim();
        }

        // ai.model.{provider,id} → execution.{provider,model}
        if (aiBlock.TryGetValue("model", out var modelRaw) && modelRaw is Dictionary<string, object?> modelMap)
        {
            if (!result.ContainsKey("provider")
                && modelMap.TryGetValue("provider", out var prov)
                && prov is string p && !string.IsNullOrWhiteSpace(p))
            {
                result["provider"] = p.Trim();
            }
            if (!result.ContainsKey("model")
                && modelMap.TryGetValue("id", out var mid)
                && mid is string m && !string.IsNullOrWhiteSpace(m))
            {
                result["model"] = m.Trim();
            }
        }

        // ai.environment.image → execution.image (fallback only — a top-level
        // `execution.image` always wins).
        if (!result.ContainsKey("image")
            && aiBlock.TryGetValue("environment", out var envRaw)
            && envRaw is Dictionary<string, object?> envMap
            && envMap.TryGetValue("image", out var img)
            && img is string i && !string.IsNullOrWhiteSpace(i))
        {
            result["image"] = i.Trim();
        }

        return result;
    }

    private static string? ScalarValue(YamlMappingNode node, string key)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode(key), out var value)) return null;
        return value is YamlScalarNode scalar ? scalar.Value : null;
    }

    private static object? ToObject(YamlNode node)
    {
        return node switch
        {
            YamlScalarNode s => s.Value,
            YamlMappingNode m => m.Children.ToDictionary(
                kvp => (kvp.Key as YamlScalarNode)?.Value ?? string.Empty,
                kvp => ToObject(kvp.Value)),
            YamlSequenceNode seq => seq.Children.Select(ToObject).ToList(),
            _ => null,
        };
    }
}

/// <summary>
/// Thrown by <see cref="DefaultPackageArtefactActivator"/> when an umbrella
/// unit's <c>members:</c> entry names a peer artefact that is neither a
/// local symbol in the same install batch nor an entry already in the
/// tenant directory. Surfaces from <see cref="PackageInstallService"/>'s
/// Phase-2 failure handling so the operator sees a precise message rather
/// than the install silently leaving members stranded at the top of the
/// Explorer tree (the symptom of issue #1664).
/// </summary>
public class UmbrellaMemberNotFoundException : Exception
{
    /// <summary>
    /// Initialises a new <see cref="UmbrellaMemberNotFoundException"/>.
    /// </summary>
    /// <param name="reference">
    /// The unresolved <c>members[]</c> reference value (the local symbol
    /// or display name as it appears in the unit YAML).
    /// </param>
    /// <param name="scheme">
    /// The address scheme of the missing artefact (<c>"unit"</c> or
    /// <c>"agent"</c>) — preserved on the exception so callers can shape
    /// downstream diagnostics without re-parsing the message.
    /// </param>
    public UmbrellaMemberNotFoundException(string reference, string scheme)
        : base($"UmbrellaMemberNotFound: '{reference}' (scheme: {scheme}). " +
            "The umbrella unit names a member that is neither a peer artefact " +
            "in this install batch nor an existing entry in the tenant directory. " +
            "Either add the member to the package or install it first.")
    {
        Reference = reference;
        Scheme = scheme;
    }

    /// <summary>The unresolved <c>members[]</c> reference value.</summary>
    public string Reference { get; }

    /// <summary>The address scheme of the missing artefact.</summary>
    public string Scheme { get; }
}
