// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Connectors;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IToolGrantResolver"/> implementation (#2335 Sub B).
/// Walks the four provenance tiers — platform (implicit <c>sv.*</c>),
/// connector grants from <see cref="UnitConnectorBindingEntity"/>
/// (inherited up the unit chain), image tools (defensive read via
/// <see cref="IImageToolsReader"/>), and explicit rows in
/// <c>agent_tool_grants</c> / <c>unit_tool_grants</c> — and folds them
/// into a single flat list with provenance metadata.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton via <c>TryAddSingleton</c> so the cloud
/// overlay can decorate (audit logging, per-tenant policy) or replace
/// outright. The resolver itself opens a fresh DI scope per call so
/// scoped EF / repository dependencies resolve cleanly from the
/// singleton instance, mirroring <see cref="UnitConnectorBindingStore"/>.
/// </para>
/// <para>
/// Conflict resolution: when the same canonical tool name appears in
/// more than one tier, the highest-precedence row wins per
/// <see cref="IToolGrantResolver"/>'s contract (explicit &gt; connector
/// &gt; platform &gt; image). The implementation emits one row per
/// unique tool id, picking the strongest provenance seen for that id
/// across every tier.
/// </para>
/// </remarks>
public sealed class ToolGrantResolver : IToolGrantResolver
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEnumerable<ISkillRegistry> _registries;
    private readonly IEnumerable<IConnectorType> _connectorTypes;
    private readonly ILogger<ToolGrantResolver> _logger;

    /// <summary>Builds the resolver with its singleton dependencies.</summary>
    public ToolGrantResolver(
        IServiceScopeFactory scopeFactory,
        IEnumerable<ISkillRegistry> registries,
        IEnumerable<IConnectorType> connectorTypes,
        ILogger<ToolGrantResolver> logger)
    {
        _scopeFactory = scopeFactory;
        _registries = registries;
        _connectorTypes = connectorTypes;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<EffectiveTool>> ResolveAsync(
        Address subject, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);

        if (!IsSubjectScheme(subject.Scheme))
        {
            throw new SpringException(
                $"Subject scheme '{subject.Scheme}' is not supported by IToolGrantResolver. " +
                "Use Address.AgentScheme or Address.UnitScheme.");
        }

        var subjectId = subject.Id;
        var isUnit = string.Equals(subject.Scheme, Address.UnitScheme, StringComparison.Ordinal);

        // Per-row carrier; we walk every tier and let the highest-precedence
        // tier win at the end. Iteration order is platform → image →
        // connector → explicit so the strongest tier is the last write,
        // matching the precedence contract on IToolGrantResolver.
        var byName = new Dictionary<string, EffectiveTool>(StringComparer.Ordinal);

        // Tier 1 (lowest precedence apart from image): platform — every
        // sv.* tool, implicitly granted.
        foreach (var entry in EnumeratePlatformTools())
        {
            byName[entry.Name] = entry;
        }

        // Tier 4 (lowest precedence): image. We seed image first so
        // higher tiers can overwrite, but emit it explicitly below to
        // keep the read-order monotonic.
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var imageReader = scope.ServiceProvider.GetRequiredService<IImageToolsReader>();
            IReadOnlyList<ImageToolEntry> imageTools;
            try
            {
                imageTools = await imageReader.GetImageToolsAsync(subject, cancellationToken);
            }
            catch (Exception ex)
            {
                // Sub C's image-tools storage is additive; if the column
                // isn't there yet the reader may throw. Treat that as
                // "no image tools" rather than failing the resolve.
                _logger.LogDebug(ex,
                    "Image-tools read failed for subject {Subject}; treating as empty.",
                    subject);
                imageTools = Array.Empty<ImageToolEntry>();
            }

            foreach (var image in imageTools)
            {
                var ns = ToolNaming.GetNamespace(image.Name);
                byName[image.Name] = new EffectiveTool(
                    Name: image.Name,
                    Namespace: ns,
                    Description: image.Description,
                    Provenance: ToolProvenance.ImagePrefix + image.ImageDigest,
                    InheritedFromUnitName: null);
            }
        }

        // Build the subject's parent-unit chain so connector grants and
        // explicit grants inherited from a parent surface with the
        // InheritedFromUnitName populated.
        var ancestorChain = await BuildAncestorChainAsync(subjectId, isUnit, cancellationToken);

        // Tier 2: connector grants. Walk every binding on the subject
        // itself (if it's a unit) plus every ancestor unit; emit one
        // EffectiveTool per <ToolNamespace>.* tool exposed by the
        // bound connector type. Lower-precedence than explicit; higher
        // than platform / image.
        foreach (var (unitId, displayName, isDirect) in ancestorChain)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var bindingRepo = scope.ServiceProvider.GetRequiredService<IUnitConnectorBindingRepository>();
            var binding = await bindingRepo.GetAsync(unitId, cancellationToken);
            if (binding is null)
            {
                continue;
            }

            var connectorType = ResolveConnectorType(binding.TypeId);
            if (connectorType is null)
            {
                _logger.LogWarning(
                    "Unit {UnitId} binds connector type {TypeId} which has no registered IConnectorType. " +
                    "Skipping namespace grant.",
                    unitId, binding.TypeId);
                continue;
            }

            var ns = connectorType.ToolNamespace;
            var slug = connectorType.Slug;
            var provenance = ToolProvenance.ConnectorPrefix + slug;
            var inheritedFrom = isDirect ? null : displayName;

            foreach (var tool in EnumerateToolsInNamespace(ns))
            {
                // Don't downgrade an explicit row that was already
                // captured. (Explicit is processed later, but we may
                // also have seeded platform / image entries first; for
                // those the connector value is stronger.)
                if (byName.TryGetValue(tool.Name, out var existing)
                    && IsStrongerOrEqual(existing.Provenance, provenance))
                {
                    continue;
                }
                byName[tool.Name] = new EffectiveTool(
                    Name: tool.Name,
                    Namespace: ns,
                    Description: tool.Description,
                    Provenance: provenance,
                    InheritedFromUnitName: inheritedFrom);
            }
        }

        // Tier 3 (highest precedence): explicit rows on the subject itself.
        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<SpringDbContext>();
            var rows = isUnit
                ? await db.UnitToolGrants
                    .AsNoTracking()
                    .Where(g => g.UnitId == subjectId)
                    .Select(g => new GrantRow(g.ToolName, g.Namespace, g.Provenance))
                    .ToListAsync(cancellationToken)
                : await db.AgentToolGrants
                    .AsNoTracking()
                    .Where(g => g.AgentId == subjectId)
                    .Select(g => new GrantRow(g.ToolName, g.Namespace, g.Provenance))
                    .ToListAsync(cancellationToken);

            // Build a description lookup off the live registries so the
            // explicit-row surface still carries the tool's description
            // (the row itself only stores the canonical name).
            var descriptions = BuildToolDescriptionIndex();

            foreach (var row in rows)
            {
                // The persisted "connector:<slug>" / "image:<digest>"
                // provenance values on the subject row are the same as
                // the values surfaced by the live registry walk above —
                // there's no value in returning duplicates. Skip those
                // here; explicit operator rows ("explicit") and any
                // future provenance value we don't recognise still
                // surface.
                if (row.Provenance.StartsWith(ToolProvenance.ConnectorPrefix, StringComparison.Ordinal)
                    || row.Provenance.StartsWith(ToolProvenance.ImagePrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var description = descriptions.TryGetValue(row.ToolName, out var d) ? d : string.Empty;
                byName[row.ToolName] = new EffectiveTool(
                    Name: row.ToolName,
                    Namespace: row.Namespace,
                    Description: description,
                    Provenance: row.Provenance,
                    InheritedFromUnitName: null);
            }
        }

        // Stable sort so callers see a deterministic order in logs and
        // UI. Namespace first, then tool name.
        return byName.Values
            .OrderBy(t => t.Namespace, StringComparer.Ordinal)
            .ThenBy(t => t.Name, StringComparer.Ordinal)
            .ToList();
    }

    private IEnumerable<EffectiveTool> EnumeratePlatformTools()
    {
        foreach (var registry in _registries)
        {
            foreach (var tool in registry.GetToolsByNamespace("sv"))
            {
                yield return new EffectiveTool(
                    Name: tool.Name,
                    Namespace: "sv",
                    Description: tool.Description,
                    Provenance: ToolProvenance.Platform,
                    InheritedFromUnitName: null);
            }
        }
    }

    private IEnumerable<ToolDefinition> EnumerateToolsInNamespace(string ns)
    {
        if (string.IsNullOrEmpty(ns))
        {
            yield break;
        }
        foreach (var registry in _registries)
        {
            foreach (var tool in registry.GetToolsByNamespace(ns))
            {
                yield return tool;
            }
        }
    }

    private IConnectorType? ResolveConnectorType(Guid typeId)
    {
        foreach (var ct in _connectorTypes)
        {
            if (ct.TypeId == typeId)
            {
                return ct;
            }
        }
        return null;
    }

    private Dictionary<string, string> BuildToolDescriptionIndex()
    {
        var index = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var registry in _registries)
        {
            foreach (var tool in registry.GetToolDefinitions())
            {
                // First registry wins on collision; the duplicate-id
                // case is structurally rejected by ToolNaming and the
                // host-level registry contract check, so this is just
                // belt-and-braces.
                index.TryAdd(tool.Name, tool.Description);
            }
        }
        return index;
    }

    private async Task<IReadOnlyList<(Guid UnitId, string DisplayName, bool IsDirect)>> BuildAncestorChainAsync(
        Guid subjectId, bool isUnit, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var memberships = sp.GetRequiredService<IUnitMembershipRepository>();
        var subunits = sp.GetRequiredService<IUnitSubunitMembershipRepository>();
        var db = sp.GetRequiredService<SpringDbContext>();

        var chain = new List<(Guid UnitId, string DisplayName, bool IsDirect)>();
        var visited = new HashSet<Guid>();

        // Seed the frontier with the subject's direct parents (or, if
        // the subject is itself a unit, the unit and then its parents).
        // For an agent subject, the binding always lives on a parent
        // unit — agents have no own binding — so even the directly-
        // joined parent counts as "inherited" from the agent's
        // perspective. For a unit subject, the unit itself is the
        // binding-bearer and any binding on it is direct; bindings on
        // ancestor units flow down as inherited.
        var frontier = new Queue<(Guid UnitId, bool IsDirect)>();
        if (isUnit)
        {
            frontier.Enqueue((subjectId, true));
        }
        else
        {
            var rows = await memberships.ListByAgentAsync(subjectId, cancellationToken);
            foreach (var row in rows)
            {
                frontier.Enqueue((row.UnitId, false));
            }
        }

        while (frontier.Count > 0)
        {
            var (unitId, isDirect) = frontier.Dequeue();
            if (!visited.Add(unitId))
            {
                continue;
            }
            var displayName = await db.UnitDefinitions
                .AsNoTracking()
                .Where(u => u.Id == unitId)
                .Select(u => u.DisplayName)
                .FirstOrDefaultAsync(cancellationToken) ?? string.Empty;
            chain.Add((unitId, displayName, isDirect));

            var parents = await subunits.ListByChildAsync(unitId, cancellationToken);
            foreach (var p in parents)
            {
                // Tenant-root edges (parent == tenant) are terminal — they
                // surface as nodes the walker can identify but don't carry
                // bindings of their own. Skip them.
                if (p.ParentId == unitId)
                {
                    continue;
                }
                frontier.Enqueue((p.ParentId, false));
            }
        }
        return chain;
    }

    private static bool IsSubjectScheme(string scheme) =>
        string.Equals(scheme, Address.AgentScheme, StringComparison.Ordinal)
        || string.Equals(scheme, Address.UnitScheme, StringComparison.Ordinal);

    private static bool IsStrongerOrEqual(string existing, string candidate)
    {
        // Precedence order (lowest → highest): image < platform < connector < explicit.
        static int Rank(string p)
        {
            if (p.StartsWith(ToolProvenance.ImagePrefix, StringComparison.Ordinal)) return 0;
            if (string.Equals(p, ToolProvenance.Platform, StringComparison.Ordinal)) return 1;
            if (p.StartsWith(ToolProvenance.ConnectorPrefix, StringComparison.Ordinal)) return 2;
            if (string.Equals(p, ToolProvenance.Explicit, StringComparison.Ordinal)) return 3;
            return 0;
        }
        return Rank(existing) >= Rank(candidate);
    }

    private readonly record struct GrantRow(string ToolName, string Namespace, string Provenance);
}
