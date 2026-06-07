// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Connectors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Manifest;

using Microsoft.EntityFrameworkCore;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Reconstructs a re-installable <c>package.yaml</c> document from the
/// platform's runtime/DB stores (ADR supersedes ADR-0035 dec 12; #3090).
///
/// <para>
/// Runtime/DB config is the single source of truth: every post-deploy edit —
/// rename, instructions, model swap, hosting change, membership/role,
/// expertise, policy, connector reconfig, secret rotation — lives in the
/// relational stores, not in the original install blob. Export therefore
/// renders the package from those stores rather than replaying the captured
/// manifest verbatim. The captured <c>original_manifest_yaml</c> remains only
/// as install-replay provenance for the retry/abort path (see #3090 R4), never
/// as the export source.
/// </para>
///
/// <para>
/// The reconstructor walks the artefact graph rooted at the install's
/// top-level unit (or single top-level agent), assembling typed
/// <see cref="UnitManifest"/> / <see cref="AgentManifest"/> documents and
/// serialising them with the same camelCase convention the manifest parser
/// consumes, so a reconstructed package re-parses and re-installs. Connector
/// bindings render as <c>requires:</c> entries (the durable
/// <c>connector_type</c> only) — connector <c>config</c> and any bound secret
/// are emitted as placeholders, never as cleartext.
/// </para>
/// </summary>
internal sealed class PackageReconstructor(
    SpringDbContext db,
    IUnitMembershipRepository membershipRepository,
    IUnitPolicyRepository policyRepository,
    IUnitConnectorBindingStore connectorBindingStore,
    IAgentDefinitionProvider definitionProvider,
    IReadOnlyDictionary<Guid, string> connectorSlugsByTypeId)
{
    private const string ApiVersion = "spring.voyage/v1";

    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new RequirementEntryYamlConverter())
        .WithTypeConverter(new InlineArtefactDefinitionYamlConverter())
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull | DefaultValuesHandling.OmitEmptyCollections)
        .DisableAliases()
        .Build();

    /// <summary>
    /// Reconstructs the package whose top-level artefact is the unit with the
    /// supplied id. Returns <c>null</c> when no such unit row exists in the
    /// current tenant scope.
    /// </summary>
    public async Task<ReconstructedPackage?> ReconstructFromUnitAsync(
        Guid unitId,
        string packageName,
        CancellationToken cancellationToken)
    {
        var unit = await db.UnitDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == unitId && u.DeletedAt == null, cancellationToken);
        if (unit is null)
        {
            return null;
        }

        var documents = new List<RenderedDocument>();
        var visited = new HashSet<Guid>();
        await RenderUnitTreeAsync(unit, documents, visited, parentFolder: $"units/{Slug(unit.DisplayName, unit.Id)}", cancellationToken);

        var package = BuildPackageDocument(packageName, unit.DisplayName, unit.Description);
        return new ReconstructedPackage(packageName, package, documents);
    }

    /// <summary>
    /// Reconstructs the package whose top-level artefact is a single agent
    /// (the AgentPackage shape). Returns <c>null</c> when no such agent row
    /// exists in the current tenant scope.
    /// </summary>
    public async Task<ReconstructedPackage?> ReconstructFromAgentAsync(
        Guid agentId,
        string packageName,
        CancellationToken cancellationToken)
    {
        var agent = await db.AgentDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == agentId && a.DeletedAt == null, cancellationToken);
        if (agent is null)
        {
            return null;
        }

        var documents = new List<RenderedDocument>
        {
            new($"agents/{Slug(agent.DisplayName, agent.Id)}/package.yaml",
                await RenderAgentAsync(agent.Id, agent.DisplayName, agent.Description, cancellationToken)),
        };

        var package = BuildPackageDocument(packageName, agent.DisplayName, agent.Description);
        return new ReconstructedPackage(packageName, package, documents);
    }

    // ── Unit tree ────────────────────────────────────────────────────────────

    private async Task RenderUnitTreeAsync(
        UnitDefinitionEntity unit,
        List<RenderedDocument> documents,
        HashSet<Guid> visited,
        string parentFolder,
        CancellationToken cancellationToken)
    {
        if (!visited.Add(unit.Id))
        {
            // Defensive: the membership graph should be acyclic, but never
            // recurse twice through the same node.
            return;
        }

        var (yaml, memberAgents, memberSubunits) =
            await RenderUnitAsync(unit, cancellationToken);
        documents.Add(new RenderedDocument($"{parentFolder}/package.yaml", yaml));

        // Member agents nest under <unit>/agents/<agent>/package.yaml so the
        // recursive ADR-0043 walker re-discovers them as members of this unit.
        foreach (var agentId in memberAgents)
        {
            var agent = await db.AgentDefinitions
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == agentId && a.DeletedAt == null, cancellationToken);
            if (agent is null)
            {
                continue;
            }
            var folder = $"{parentFolder}/agents/{Slug(agent.DisplayName, agent.Id)}";
            documents.Add(new RenderedDocument(
                $"{folder}/package.yaml",
                await RenderAgentAsync(agent.Id, agent.DisplayName, agent.Description, cancellationToken)));
        }

        // Sub-units nest recursively.
        foreach (var subUnitId in memberSubunits)
        {
            var subUnit = await db.UnitDefinitions
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == subUnitId && u.DeletedAt == null, cancellationToken);
            if (subUnit is null)
            {
                continue;
            }
            await RenderUnitTreeAsync(
                subUnit, documents, visited,
                $"{parentFolder}/units/{Slug(subUnit.DisplayName, subUnit.Id)}",
                cancellationToken);
        }
    }

    private async Task<(string Yaml, IReadOnlyList<Guid> MemberAgents, IReadOnlyList<Guid> MemberSubunits)> RenderUnitAsync(
        UnitDefinitionEntity unit,
        CancellationToken cancellationToken)
    {
        // Pull the live dispatch projection (definition jsonb + live-config /
        // execution) so the rendered execution/model/hosting reflect edits —
        // the same source the dispatcher reads. This is the heart of #3090:
        // export sees exactly what dispatch sees, not the install blob.
        var projected = await definitionProvider.GetByIdAsync(GuidFormatter.Format(unit.Id), cancellationToken);

        var memberships = await membershipRepository.ListByUnitAsync(unit.Id, cancellationToken);
        var subunitEdges = await db.UnitSubunitMemberships
            .AsNoTracking()
            .Where(e => e.ParentId == unit.Id)
            .Select(e => e.ChildId)
            .ToListAsync(cancellationToken);

        var members = new List<MemberManifest>();
        var memberAgentIds = new List<Guid>();
        foreach (var membership in memberships)
        {
            // Reference each member agent by the symbol the nested folder
            // exposes (its slug); the install resolver maps the local symbol
            // to a freshly-minted Guid. Per-membership role overrides ride on
            // the member entry.
            var agent = await db.AgentDefinitions
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == membership.AgentId && a.DeletedAt == null, cancellationToken);
            if (agent is null)
            {
                continue;
            }
            memberAgentIds.Add(agent.Id);
            members.Add(new MemberManifest
            {
                Agent = InlineArtefactDefinition.FromReference(Slug(agent.DisplayName, agent.Id)),
            });
        }

        foreach (var childId in subunitEdges)
        {
            var child = await db.UnitDefinitions
                .AsNoTracking()
                .FirstOrDefaultAsync(u => u.Id == childId && u.DeletedAt == null, cancellationToken);
            if (child is null)
            {
                continue;
            }
            members.Add(new MemberManifest
            {
                Unit = InlineArtefactDefinition.FromReference(Slug(child.DisplayName, child.Id)),
            });
        }

        var manifest = new UnitManifest
        {
            ApiVersion = ApiVersion,
            Kind = "Unit",
            Name = Slug(unit.DisplayName, unit.Id),
            DisplayName = unit.DisplayName,
            Description = NullIfEmpty(unit.Description),
            Role = NullIfEmpty(unit.Role),
            Instructions = ReadInstructions(unit.Definition),
            Ai = BuildAi(projected?.Execution),
            Execution = BuildExecution(projected?.Execution),
            Members = members.Count == 0 ? null : members,
            Expertise = await BuildUnitExpertiseAsync(unit.Id, cancellationToken),
            Policies = await BuildPoliciesAsync(unit.Id, cancellationToken),
            Requires = await BuildRequiresAsync(unit.Id, cancellationToken),
        };

        return (Serialize(manifest), memberAgentIds, subunitEdges);
    }

    private async Task<string> RenderAgentAsync(
        Guid agentId,
        string displayName,
        string? description,
        CancellationToken cancellationToken)
    {
        var projected = await definitionProvider.GetByIdAsync(GuidFormatter.Format(agentId), cancellationToken);
        var agentRow = await db.AgentDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == agentId && a.DeletedAt == null, cancellationToken);

        var manifest = new AgentManifest
        {
            ApiVersion = ApiVersion,
            Kind = "Agent",
            Name = Slug(displayName, agentId),
            DisplayName = displayName,
            Description = NullIfEmpty(description) ?? string.Empty,
            Role = NullIfEmpty(projected?.Role) ?? NullIfEmpty(agentRow?.Role),
            Instructions = projected?.Instructions ?? ReadInstructions(agentRow?.Definition),
            Ai = BuildAi(projected?.Execution),
            Execution = BuildAgentExecution(projected?.Execution),
            Expertise = BuildExpertise(projected?.Expertise),
        };

        return Serialize(manifest);
    }

    // ── Field builders ─────────────────────────────────────────────────────

    private static PackageManifest BuildPackageDocument(string packageName, string? displayName, string? description)
        => new()
        {
            ApiVersion = ApiVersion,
            Kind = "Package",
            Name = packageName,
            DisplayName = NullIfEmpty(displayName),
            Description = NullIfEmpty(description) ?? packageName,
            Version = "1.0.0",
        };

    private static AiManifest? BuildAi(AgentExecutionConfig? execution)
    {
        if (execution is null)
        {
            return null;
        }
        var hasRuntime = !string.IsNullOrWhiteSpace(execution.Runtime);
        var hasModel = execution.Model is not null;
        if (!hasRuntime && !hasModel)
        {
            return null;
        }
        return new AiManifest
        {
            Runtime = hasRuntime ? execution.Runtime : null,
            Model = execution.Model is null
                ? null
                : new AiModelManifest { Provider = execution.Model.Provider, Id = execution.Model.Id },
        };
    }

    private static ExecutionManifest? BuildExecution(AgentExecutionConfig? execution)
    {
        if (execution is null)
        {
            return null;
        }
        var block = new ExecutionManifest
        {
            Image = NullIfEmpty(execution.Image),
            Hosting = HostingLiteral(execution.Hosting),
            SystemPromptMode = execution.SystemPromptMode?.ToString().ToLowerInvariant(),
        };
        return block.IsEmpty ? null : block;
    }

    private static AgentExecutionManifest? BuildAgentExecution(AgentExecutionConfig? execution)
    {
        if (execution is null)
        {
            return null;
        }
        var block = new AgentExecutionManifest
        {
            Image = NullIfEmpty(execution.Image),
            Hosting = HostingLiteral(execution.Hosting),
            SystemPromptMode = execution.SystemPromptMode?.ToString().ToLowerInvariant(),
        };
        return block.IsEmpty ? null : block;
    }

    private static List<ExpertiseManifestEntry>? BuildExpertise(IReadOnlyList<ExpertiseDomain>? expertise)
    {
        if (expertise is null || expertise.Count == 0)
        {
            return null;
        }
        return expertise
            .Select(e => new ExpertiseManifestEntry
            {
                Domain = e.Name,
                Description = NullIfEmpty(e.Description),
                Level = e.Level?.ToString().ToLowerInvariant(),
            })
            .ToList();
    }

    private async Task<List<ExpertiseManifestEntry>?> BuildUnitExpertiseAsync(
        Guid unitId,
        CancellationToken cancellationToken)
    {
        var rows = await db.UnitExpertise
            .AsNoTracking()
            .Where(e => e.UnitId == unitId)
            .OrderBy(e => e.Name)
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return null;
        }
        return rows
            .Select(e => new ExpertiseManifestEntry
            {
                Domain = e.Name,
                Description = NullIfEmpty(e.Description),
                Level = e.Level?.ToString().ToLowerInvariant(),
            })
            .ToList();
    }

    private async Task<Dictionary<string, object>?> BuildPoliciesAsync(
        Guid unitId,
        CancellationToken cancellationToken)
    {
        var policy = await policyRepository.GetAsync(unitId, cancellationToken);
        if (policy is null || policy.IsEmpty)
        {
            return null;
        }

        var map = new Dictionary<string, object>();
        if (policy.Skill is { } skill && (skill.Allowed is not null || skill.Blocked is not null))
        {
            map["skill"] = AllowBlock(skill.Allowed, skill.Blocked);
        }
        if (policy.Model is { } model && (model.Allowed is not null || model.Blocked is not null))
        {
            map["model"] = AllowBlock(model.Allowed, model.Blocked);
        }
        if (policy.Cost is { } cost &&
            (cost.MaxCostPerInvocation is not null || cost.MaxCostPerHour is not null || cost.MaxCostPerDay is not null))
        {
            var costMap = new Dictionary<string, object>();
            if (cost.MaxCostPerInvocation is { } perInvoke)
            {
                costMap["maxCostPerInvocation"] = perInvoke;
            }
            if (cost.MaxCostPerHour is { } perHour)
            {
                costMap["maxCostPerHour"] = perHour;
            }
            if (cost.MaxCostPerDay is { } perDay)
            {
                costMap["maxCostPerDay"] = perDay;
            }
            map["cost"] = costMap;
        }
        if (policy.ExecutionMode is { } exec && (exec.Forced is not null || exec.Allowed is not null))
        {
            var execMap = new Dictionary<string, object>();
            if (exec.Forced is { } forced)
            {
                execMap["forced"] = forced.ToString().ToLowerInvariant();
            }
            if (exec.Allowed is not null)
            {
                execMap["allowed"] = exec.Allowed.Select(m => m.ToString().ToLowerInvariant()).ToList();
            }
            map["executionMode"] = execMap;
        }

        return map.Count == 0 ? null : map;
    }

    private async Task<List<RequirementEntry>?> BuildRequiresAsync(
        Guid unitId,
        CancellationToken cancellationToken)
    {
        // The durable connector binding is 1:1 with the unit. Render it as a
        // `requires: [{ connector: <slug> }]` entry — the connector *type*
        // only. Connector config and any bound secret are deliberately NOT
        // exported (a re-install re-binds and re-supplies secrets via
        // placeholders), so export never leaks a credential.
        var binding = await connectorBindingStore.GetAsync(unitId, cancellationToken);
        if (binding is null)
        {
            return null;
        }

        // unit_connector_bindings persists the connector's stable TypeId; the
        // requires: grammar references it by Slug. Resolve via the registry
        // map the export service supplies (registry resolution is cycle-safe
        // because it is built from a fresh scope, not constructor-injected
        // downstream of the binding store).
        if (!connectorSlugsByTypeId.TryGetValue(binding.TypeId, out var slug)
            || string.IsNullOrWhiteSpace(slug))
        {
            return null;
        }

        return new List<RequirementEntry>
        {
            new() { Type = RequirementType.Connector, Identifier = slug },
        };
    }

    // ── Serialisation helpers ──────────────────────────────────────────────

    private static string Serialize(object manifest)
    {
        var body = Serializer.Serialize(manifest);
        // Ensure a trailing newline for clean concatenation / file write.
        return body.EndsWith('\n') ? body : body + "\n";
    }

    private static Dictionary<string, object> AllowBlock(
        IReadOnlyList<string>? allowed,
        IReadOnlyList<string>? blocked)
    {
        var map = new Dictionary<string, object>();
        if (allowed is not null)
        {
            map["allowed"] = allowed.ToList();
        }
        if (blocked is not null)
        {
            map["blocked"] = blocked.ToList();
        }
        return map;
    }

    private static string? ReadInstructions(JsonElement? definition)
    {
        if (definition is not { ValueKind: JsonValueKind.Object } element)
        {
            return null;
        }
        if (element.TryGetProperty("instructions", out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            var value = prop.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }
        return null;
    }

    private static string? HostingLiteral(AgentHostingMode hosting)
        => hosting switch
        {
            AgentHostingMode.Ephemeral => "ephemeral",
            AgentHostingMode.Pooled => "pooled",
            // Persistent is the platform default; emit it explicitly so a
            // re-install of an edited unit preserves an operator's deliberate
            // persistent selection rather than relying on the default.
            AgentHostingMode.Persistent => "persistent",
            _ => null,
        };

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>
    /// Derives a stable local symbol (folder name + member reference) from a
    /// display name, falling back to the artefact's no-dash Guid when the
    /// display name has no symbol-safe characters. The symbol only needs to be
    /// unique within the reconstructed package and re-parse cleanly; the
    /// install resolver mints a fresh Guid identity regardless.
    /// </summary>
    private static string Slug(string? displayName, Guid id)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return GuidFormatter.Format(id);
        }

        var sb = new StringBuilder(displayName.Length);
        foreach (var ch in displayName.Trim().ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
            }
            else if (ch is ' ' or '-' or '_' or '.' or '/')
            {
                sb.Append('-');
            }
        }

        // Collapse runs of '-' and trim leading/trailing separators.
        var collapsed = new StringBuilder(sb.Length);
        var lastWasDash = false;
        foreach (var ch in sb.ToString())
        {
            if (ch == '-')
            {
                if (!lastWasDash && collapsed.Length > 0)
                {
                    collapsed.Append('-');
                }
                lastWasDash = true;
            }
            else
            {
                collapsed.Append(ch);
                lastWasDash = false;
            }
        }

        var result = collapsed.ToString().Trim('-');
        return string.IsNullOrEmpty(result) ? GuidFormatter.Format(id) : result;
    }
}

/// <summary>
/// A reconstructed package: the package metadata document plus every artefact
/// document, each addressed by its relative path inside the package tree.
/// </summary>
/// <param name="PackageName">The package name (root folder + manifest name).</param>
/// <param name="Package">The reconstructed root <c>package.yaml</c> document.</param>
/// <param name="Documents">
/// Every artefact document under the package root, keyed by relative path
/// (e.g. <c>units/team/package.yaml</c>).
/// </param>
public sealed record ReconstructedPackage(
    string PackageName,
    PackageManifest Package,
    IReadOnlyList<RenderedDocument> Documents);

/// <summary>One reconstructed YAML document and its relative path in the tree.</summary>
/// <param name="RelativePath">Path under the package root (forward-slash separated).</param>
/// <param name="Yaml">The serialised YAML body.</param>
public sealed record RenderedDocument(string RelativePath, string Yaml);
