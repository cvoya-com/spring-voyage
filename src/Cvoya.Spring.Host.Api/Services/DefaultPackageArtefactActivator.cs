// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Manifest;

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
    private readonly ILogger<DefaultPackageArtefactActivator> _logger;

    /// <summary>
    /// Initialises a new <see cref="DefaultPackageArtefactActivator"/>.
    /// </summary>
    public DefaultPackageArtefactActivator(
        IUnitCreationService unitCreationService,
        IDirectoryService directoryService,
        IServiceScopeFactory scopeFactory,
        ILogger<DefaultPackageArtefactActivator> logger)
    {
        _unitCreationService = unitCreationService;
        _directoryService = directoryService;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ActivateAsync(
        string packageName,
        ResolvedArtefact artefact,
        Guid installId,
        LocalSymbolMap symbolMap,
        IReadOnlyDictionary<string, ConnectorBinding>? connectorBindings = null,
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
                await ActivateUnitAsync(artefact, symbolMap, connectorBindings, cancellationToken);
                break;

            case ArtefactKind.Agent:
                await ActivateAgentAsync(artefact, symbolMap, cancellationToken);
                break;

            case ArtefactKind.Skill:
            case ArtefactKind.Workflow:
                // Skills and workflows are registered via other paths that
                // read from disk at resolution time; no actor-activation step.
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
        CancellationToken ct)
    {
        var manifest = ManifestParser.Parse(artefact.Content!);

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

        var overrides = new UnitCreationOverrides(IsTopLevel: true, ActorId: actorId);

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
    }

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
            if (!string.IsNullOrWhiteSpace(member.Unit))
            {
                // Unit-typed members are the heart of #1664. An unresolvable
                // unit reference would mint a phantom Guid that has no
                // corresponding unit_definitions row — the failure mode is
                // a "stranded" sub-unit hierarchy in the Explorer tree.
                // Fail loudly here so the operator hears about it.
                (member.Unit, directoryEntries) = await ResolveUnitReferenceAsync(
                    member.Unit!, symbolMap, directoryEntries, ct);
            }
            else if (!string.IsNullOrWhiteSpace(member.Agent))
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
                (member.Agent, directoryEntries) = await ResolveAgentReferenceAsync(
                    member.Agent!, symbolMap, directoryEntries, ct);
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
        CancellationToken ct)
    {
        var content = artefact.Content!;
        AgentManifestFields fields;
        try
        {
            fields = ParseAgentManifest(content);
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
            _logger.LogWarning(
                "Agent artefact has no name; cannot register in directory.");
            return;
        }

        var displayName = string.IsNullOrWhiteSpace(fields.DisplayName) ? slug : fields.DisplayName!;
        var description = fields.Description ?? string.Empty;

        // #1629 PR7: resolve the agent's identity through the local-symbol
        // map so a re-run of activation reuses the pre-allocated Guid. The
        // directory's own ResolveAsync still wins when the agent already has
        // a registration (e.g. retry after Phase-2 partial failure).
        var address = Address.For("agent", slug);
        var existing = await _directoryService.ResolveAsync(address, ct);
        var actorId = existing?.ActorId ?? symbolMap.GetOrMint(ArtefactKind.Agent, artefact.Name);

        var entry = new DirectoryEntry(
            address,
            actorId,
            displayName,
            description,
            fields.Role,
            DateTimeOffset.UtcNow);

        await _directoryService.RegisterAsync(entry, ct);

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
                _logger.LogWarning(ex,
                    "Agent artefact '{Name}': directory registration succeeded but Definition JSON write failed.",
                    slug);
            }
        }

        _logger.LogInformation(
            "Agent artefact '{Name}' registered (actorId={ActorId}, role={Role}).",
            slug, actorId, fields.Role ?? "(none)");
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
    private static AgentManifestFields ParseAgentManifest(string yamlText)
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
        var displayName = ScalarValue(agentNode, "name");
        var role = ScalarValue(agentNode, "role");
        var description = ScalarValue(agentNode, "description");

        // Build the Definition JSON from execution + ai blocks if present.
        // The AgentDefinitionEntity.Definition shape is the same JSON the
        // CLI's `--definition` flag accepts.
        var defObj = new Dictionary<string, object?>();
        if (agentNode.Children.TryGetValue(new YamlScalarNode("execution"), out var execRaw)
            && execRaw is YamlMappingNode execMap)
        {
            defObj["execution"] = ToObject(execMap);
        }
        if (agentNode.Children.TryGetValue(new YamlScalarNode("ai"), out var aiRaw)
            && aiRaw is YamlMappingNode aiMap)
        {
            defObj["ai"] = ToObject(aiMap);
        }

        JsonElement? defJson = null;
        if (defObj.Count > 0)
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(defObj));
            defJson = doc.RootElement.Clone();
        }

        return new AgentManifestFields(id, displayName, role, description, defJson);
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