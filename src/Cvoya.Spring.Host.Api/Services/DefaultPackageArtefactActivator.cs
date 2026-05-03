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
                await ActivateUnitAsync(artefact, cancellationToken);
                break;

            case ArtefactKind.Agent:
                await ActivateAgentAsync(artefact, cancellationToken);
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

    private async Task ActivateUnitAsync(ResolvedArtefact artefact, CancellationToken ct)
    {
        var manifest = ManifestParser.Parse(artefact.Content!);
        var overrides = new UnitCreationOverrides(IsTopLevel: true);
        await _unitCreationService.CreateFromManifestAsync(manifest, overrides, ct);
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
    private async Task ActivateAgentAsync(ResolvedArtefact artefact, CancellationToken ct)
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

        var address = new Address("agent", slug);
        var existing = await _directoryService.ResolveAsync(address, ct);
        var actorId = existing?.ActorId ?? Guid.NewGuid().ToString();

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
                    .FirstOrDefaultAsync(a => a.AgentId == slug, ct);
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