// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Lifecycle;

using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

/// <summary>
/// Resolves <c>from:</c> references declared by concrete artefacts
/// (<c>Unit</c> / <c>Agent</c>) and by template chains
/// (<c>UnitTemplate from: …</c> / <c>AgentTemplate from: …</c>) per
/// ADR-0043 §5.
/// <para>
/// Stamping at install time:
/// <list type="bullet">
///   <item><description>
///     Reads the template's outer body and merges it into the consumer
///     under the merge rules — scalars: instance wins, template fills
///     gaps; maps: deep-merge with instance keys winning; lists: instance
///     replaces template's list entirely (or template's list flows
///     through when instance has no list).
///   </description></item>
///   <item><description>
///     For each concrete child artefact under the template's
///     <c>agents/</c> / <c>units/</c> subdirectories, produces a fresh
///     concrete child of the consumer with a newly minted display-name
///     identity (Guid identity per ADR-0036 is minted by the install
///     pipeline downstream — at the resolver layer we just emit fresh
///     <see cref="ResolvedArtefact"/> entries).
///   </description></item>
///   <item><description>
///     Recurses into the cloned children's own folder trees so a
///     stamped child that itself declares <c>from:</c> is also resolved.
///   </description></item>
/// </list>
/// </para>
/// <para>
/// Snapshot binding: the resolver runs once at install time and the
/// resolved tree is captured into the persisted definitions. There is
/// no live re-binding (ADR-0043 §5f).
/// </para>
/// </summary>
public interface ITemplateResolver
{
    /// <summary>
    /// Resolves every <c>from:</c> reference declared by a concrete
    /// artefact in <paramref name="package"/>. Returns a new
    /// <see cref="ResolvedPackage"/> whose <c>Units</c> / <c>Agents</c>
    /// lists contain the stamped instances plus any cloned children
    /// of those instances; templates themselves are dropped from the
    /// output (they do not activate per ADR-0043 §5a).
    /// </summary>
    Task<ResolvedPackage> ResolveAsync(
        ResolvedPackage package,
        string? packageRoot,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Default <see cref="ITemplateResolver"/>. Walks the on-disk template
/// folder to enumerate the template's concrete children (so per-package
/// templating works even when the templates ship nested artefacts that
/// don't surface in <see cref="ResolvedPackage.Agents"/> /
/// <see cref="ResolvedPackage.Units"/> at the package root). Falls back
/// to the package catalog provider for cross-package <c>from:</c>
/// references (<c>&lt;pkg&gt;/&lt;template-name&gt;@&lt;version&gt;</c>).
/// </summary>
public sealed class TemplateResolver : ITemplateResolver
{
    private readonly IPackageCatalogProvider? _catalogProvider;

    /// <summary>
    /// Initialises a new <see cref="TemplateResolver"/>.
    /// </summary>
    /// <param name="catalogProvider">
    /// Optional catalog provider used to resolve cross-package
    /// <c>from:</c> references. When <c>null</c> any cross-package
    /// <c>from:</c> raises a <see cref="PackageParseException"/>.
    /// </param>
    public TemplateResolver(IPackageCatalogProvider? catalogProvider = null)
    {
        _catalogProvider = catalogProvider;
    }

    /// <inheritdoc />
    public async Task<ResolvedPackage> ResolveAsync(
        ResolvedPackage package,
        string? packageRoot,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        // Index every discovered artefact + template folder so we can
        // look up `from:` targets by (kind, name) and also enumerate the
        // template's nested concrete children when stamping. The resolver
        // re-walks the package root because the catalog walker discards
        // templates from `ResolvedPackage.Units` / `Agents` (per
        // PackageManifestParser.ParseAndResolveAsync, units / agents
        // include both concrete and projected-template artefacts; the
        // resolver disambiguates by re-reading the inner `kind:`).
        IReadOnlyList<TemplateIndexEntry> index = string.IsNullOrEmpty(packageRoot)
            ? Array.Empty<TemplateIndexEntry>()
            : BuildIndex(packageRoot!, cancellationToken);

        // Re-classify every artefact in the input package by its inner
        // `kind:` field — concrete vs template. Templates do not flow
        // through to the output; concrete artefacts that declare `from:`
        // are stamped against the matching template.
        var outputUnits = new List<ResolvedArtefact>();
        var outputAgents = new List<ResolvedArtefact>();

        // The package's top-level artefacts are the ones whose folder
        // sits directly under `<root>/units/` or `<root>/agents/`. The
        // resolver only stamps top-level Unit / Agent artefacts here —
        // nested artefacts (a unit's own member agents) are stamped
        // recursively below via WalkAndStampChildren.
        foreach (var artefact in package.Units.Concat(package.Agents).Where(a => !a.IsCrossPackage))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Skip templates entirely — they never activate.
            var indexEntry = LookupByPath(index, artefact.ResolvedPath);
            if (indexEntry?.IsTemplate == true)
            {
                continue;
            }

            // Skip nested artefacts whose ancestor chain crosses through
            // a template — the catalog walker surfaces every nested
            // artefact (a concrete Agent at templates/team/agents/lead/
            // is discovered) but those nested artefacts are NOT directly
            // installable; they are reachable only through a consumer
            // that says `from: team`. The resolver stamps them out via
            // CollectTemplateChildren on the consumer side. Including
            // them again here would double up the cloned agents.
            if (indexEntry is not null && HasTemplateAncestor(indexEntry, index))
            {
                continue;
            }

            // Stamp the artefact through its `from:` chain (no-op when
            // the artefact has no `from:` declaration).
            var (stamped, stampedChildren) = await StampArtefactAsync(
                artefact, index, packageRoot, cancellationToken).ConfigureAwait(false);

            if (stamped.Kind == ArtefactKind.Unit)
            {
                outputUnits.Add(stamped);
            }
            else if (stamped.Kind == ArtefactKind.Agent)
            {
                outputAgents.Add(stamped);
            }

            foreach (var child in stampedChildren)
            {
                if (child.Kind == ArtefactKind.Unit)
                {
                    outputUnits.Add(child);
                }
                else if (child.Kind == ArtefactKind.Agent)
                {
                    outputAgents.Add(child);
                }
            }
        }

        // Cross-package artefacts (members that reference
        // `pkg-b/agent-x@1.0.0`) flow through untouched — they are
        // already activated in their source package.
        foreach (var artefact in package.Units.Where(a => a.IsCrossPackage))
        {
            outputUnits.Add(artefact);
        }
        foreach (var artefact in package.Agents.Where(a => a.IsCrossPackage))
        {
            outputAgents.Add(artefact);
        }

        return new ResolvedPackage
        {
            Name = package.Name,
            Description = package.Description,
            Version = package.Version,
            Kind = package.Kind,
            InputValues = package.InputValues,
            Units = outputUnits,
            Agents = outputAgents,
            Skills = package.Skills,
            HumanTemplates = package.HumanTemplates,
            RequiredConnectorSlugs = package.RequiredConnectorSlugs,
            ConnectorRequiresByArtefact = package.ConnectorRequiresByArtefact,
            Execution = package.Execution,
            Warnings = package.Warnings,
        };
    }

    // ── Index ────────────────────────────────────────────────────────────

    /// <summary>
    /// One entry in the resolver's index of artefact folders. Indexed by
    /// folder path so the resolver can correlate a
    /// <see cref="ResolvedArtefact"/> back to its inner <c>kind:</c>
    /// discriminator and its containing folder.
    /// </summary>
    private sealed record TemplateIndexEntry(
        ArtefactKind ProjectedKind,
        string DeclaredKind,
        string Name,
        string FolderPath,
        string PackageYamlPath,
        string RawYaml,
        string? From,
        string? ContainingArtefactName)
    {
        public bool IsTemplate =>
            string.Equals(DeclaredKind, "UnitTemplate", StringComparison.Ordinal)
            || string.Equals(DeclaredKind, "AgentTemplate", StringComparison.Ordinal);
    }

    private static IReadOnlyList<TemplateIndexEntry> BuildIndex(
        string packageRoot,
        CancellationToken cancellationToken)
    {
        var result = new List<TemplateIndexEntry>();
        WalkForIndex(packageRoot, containingArtefact: null, result, cancellationToken);
        return result;
    }

    private static void WalkForIndex(
        string folder,
        string? containingArtefact,
        List<TemplateIndexEntry> found,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        // Mirror PackageManifestParser.ConventionalSubdirs without taking
        // a dependency on its internal table — keeping this enumeration
        // local keeps the resolver self-contained.
        foreach (var subdirName in new[] { "units", "agents", "templates" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var subdirPath = Path.Combine(folder, subdirName);
            if (!Directory.Exists(subdirPath))
            {
                continue;
            }

            foreach (var childDir in Directory.EnumerateDirectories(subdirPath))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var manifestPath = Path.Combine(childDir, "package.yaml");
                if (!File.Exists(manifestPath))
                {
                    var alt = Path.Combine(childDir, "package.yml");
                    if (File.Exists(alt))
                    {
                        manifestPath = alt;
                    }
                    else
                    {
                        // The walker would have rejected this earlier;
                        // be defensive and skip rather than re-raise.
                        continue;
                    }
                }

                var rawYaml = File.ReadAllText(manifestPath);
                var (declaredKind, name, fromRef) = ReadIndexHeaders(rawYaml);
                if (string.IsNullOrWhiteSpace(declaredKind) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var projected = declaredKind switch
                {
                    "Unit" or "UnitTemplate" => ArtefactKind.Unit,
                    "Agent" or "AgentTemplate" => ArtefactKind.Agent,
                    _ => (ArtefactKind?)null,
                };
                if (projected is null)
                {
                    continue;
                }

                found.Add(new TemplateIndexEntry(
                    ProjectedKind: projected.Value,
                    DeclaredKind: declaredKind!,
                    Name: name!,
                    FolderPath: childDir,
                    PackageYamlPath: manifestPath,
                    RawYaml: rawYaml,
                    From: fromRef,
                    ContainingArtefactName: containingArtefact));

                // Recurse into the artefact's own conventional subdirs.
                WalkForIndex(
                    childDir,
                    containingArtefact: name,
                    found: found,
                    cancellationToken: cancellationToken);
            }
        }
    }

    private static (string? Kind, string? Name, string? From) ReadIndexHeaders(string yamlText)
    {
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            var headers = deserializer.Deserialize<IndexHeaders>(yamlText)
                ?? new IndexHeaders();
            return (headers.Kind, headers.Name, headers.From);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private sealed class IndexHeaders
    {
        [YamlMember(Alias = "kind")]
        public string? Kind { get; set; }

        [YamlMember(Alias = "name")]
        public string? Name { get; set; }

        [YamlMember(Alias = "from")]
        public string? From { get; set; }
    }

    private static TemplateIndexEntry? LookupByPath(
        IReadOnlyList<TemplateIndexEntry> index, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }
        foreach (var entry in index)
        {
            if (string.Equals(entry.PackageYamlPath, path, StringComparison.Ordinal))
            {
                return entry;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns <c>true</c> when any ancestor in <paramref name="entry"/>'s
    /// containment chain is a template. Used to filter out the catalog
    /// walker's view of templates' nested concrete children — those
    /// children are reachable only through their owning template's
    /// `from:` consumers, not as directly installable artefacts.
    /// </summary>
    private static bool HasTemplateAncestor(
        TemplateIndexEntry entry,
        IReadOnlyList<TemplateIndexEntry> index)
    {
        var current = entry.ContainingArtefactName;
        var guard = 0;
        while (!string.IsNullOrEmpty(current) && guard++ < 64)
        {
            var parent = index.FirstOrDefault(e =>
                string.Equals(e.Name, current, StringComparison.Ordinal));
            if (parent is null)
            {
                return false;
            }
            if (parent.IsTemplate)
            {
                return true;
            }
            current = parent.ContainingArtefactName;
        }
        return false;
    }

    // ── Stamping ─────────────────────────────────────────────────────────

    /// <summary>
    /// Stamps a single concrete artefact through its <c>from:</c> chain
    /// (if any). Returns the stamped artefact plus any concrete children
    /// cloned in from the template's nested <c>agents/</c> / <c>units/</c>
    /// subdirectories.
    /// </summary>
    private async Task<(ResolvedArtefact Stamped, IReadOnlyList<ResolvedArtefact> Children)> StampArtefactAsync(
        ResolvedArtefact artefact,
        IReadOnlyList<TemplateIndexEntry> index,
        string? packageRoot,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(artefact.Content))
        {
            return (artefact, Array.Empty<ResolvedArtefact>());
        }

        var fromRef = ReadFromField(artefact.Content);
        if (string.IsNullOrWhiteSpace(fromRef))
        {
            return (artefact, Array.Empty<ResolvedArtefact>());
        }

        var (mergedYaml, clonedChildren) = await StampFromChainAsync(
            consumerYaml: artefact.Content,
            consumerKind: artefact.Kind,
            fromRef: fromRef!,
            index: index,
            packageRoot: packageRoot,
            visited: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ct: ct).ConfigureAwait(false);

        var stamped = new ResolvedArtefact
        {
            Name = artefact.Name,
            SourcePackage = artefact.SourcePackage,
            Kind = artefact.Kind,
            ResolvedPath = artefact.ResolvedPath,
            Content = mergedYaml,
            ContainingArtefactName = artefact.ContainingArtefactName,
        };

        // Stamped children belong to the consumer, not to the template
        // they were cloned from. Rewriting `ContainingArtefactName` here
        // keeps the §6 "top-level vs nested" classification honest — a
        // child cloned into the consumer's tree is a nested artefact of
        // the consumer.
        var rebound = new List<ResolvedArtefact>(clonedChildren.Count);
        foreach (var child in clonedChildren)
        {
            rebound.Add(new ResolvedArtefact
            {
                Name = child.Name,
                SourcePackage = child.SourcePackage,
                Kind = child.Kind,
                ResolvedPath = child.ResolvedPath,
                Content = child.Content,
                ContainingArtefactName = child.ContainingArtefactName ?? artefact.Name,
            });
        }
        return (stamped, rebound);
    }

    /// <summary>
    /// Walks the consumer → template chain bottom-up (innermost template
    /// first), merging each template's outer body into the consumer per
    /// the ADR-0043 §5d merge rules and accumulating cloned children from
    /// every template's nested concrete artefacts.
    /// </summary>
    private async Task<(string MergedYaml, IReadOnlyList<ResolvedArtefact> Children)> StampFromChainAsync(
        string consumerYaml,
        ArtefactKind consumerKind,
        string fromRef,
        IReadOnlyList<TemplateIndexEntry> index,
        string? packageRoot,
        HashSet<string> visited,
        CancellationToken ct)
    {
        var reference = ArtefactReference.Parse(fromRef, consumerKind);

        // Locate the template entry. In-package: walk the index by name.
        // Cross-package: load via the catalog provider.
        TemplateIndexEntry? templateEntry = null;
        string templateYaml;
        string? templateFolder = null;
        if (reference.IsCrossPackage)
        {
            if (_catalogProvider is null)
            {
                throw new PackageParseException(
                    $"Cross-package template reference '{fromRef}' cannot be resolved: " +
                    "no IPackageCatalogProvider is wired into the TemplateResolver.");
            }
            var loaded = await _catalogProvider
                .LoadArtefactYamlAsync(reference.PackageName!, consumerKind, reference.ArtefactName, ct)
                .ConfigureAwait(false);
            if (loaded is null)
            {
                throw new PackageParseException(
                    $"Cross-package template '{reference.PackageName}/{reference.ArtefactName}' not found in the catalog " +
                    $"(declared via 'from: {fromRef}').");
            }
            templateYaml = loaded;
        }
        else
        {
            templateEntry = index
                .Where(e => e.ProjectedKind == consumerKind
                            && string.Equals(e.Name, reference.ArtefactName, StringComparison.Ordinal))
                .FirstOrDefault();
            if (templateEntry is null)
            {
                throw new PackageParseException(
                    $"Template '{reference.ArtefactName}' (kind {consumerKind}) referenced via 'from:' was not found in the package.");
            }
            templateYaml = templateEntry.RawYaml;
            templateFolder = templateEntry.FolderPath;
        }

        // Cycle guard: cycles are caught at parse time
        // (PackageManifestParser.DetectCycles walks `from:` edges) — this
        // is a defensive double-check.
        var visitKey = $"{consumerKind}|{(reference.PackageName ?? "<self>")}|{reference.ArtefactName}";
        if (!visited.Add(visitKey))
        {
            throw new PackageParseException(
                $"Cycle detected while resolving 'from:' chain at '{fromRef}'. " +
                "Template cycles must be rejected at parse time.");
        }

        // Resolve the template's own `from:` chain first (template
        // chaining per §5e), so the outermost merge sees the fully-
        // resolved template body.
        var templateFromRef = ReadFromField(templateYaml);
        string resolvedTemplateYaml = templateYaml;
        IReadOnlyList<ResolvedArtefact> chainedChildren = Array.Empty<ResolvedArtefact>();
        if (!string.IsNullOrWhiteSpace(templateFromRef))
        {
            var (chainedYaml, chainChildren) = await StampFromChainAsync(
                consumerYaml: templateYaml,
                consumerKind: consumerKind,
                fromRef: templateFromRef!,
                index: index,
                packageRoot: packageRoot,
                visited: visited,
                ct: ct).ConfigureAwait(false);
            resolvedTemplateYaml = chainedYaml;
            chainedChildren = chainChildren;
        }

        // Merge the (now fully resolved) template body into the
        // consumer body.
        var mergedYaml = MergeYaml(consumerYaml: consumerYaml, templateYaml: resolvedTemplateYaml);

        // Stamp out the template's nested concrete children. In-package
        // templates use the on-disk folder tree; cross-package templates
        // pull their nested children through the catalog provider's
        // EnumerateNestedArtefactsAsync method (ADR-0043 §5h
        // archetype-library case).
        var childArtefacts = new List<ResolvedArtefact>();
        if (templateEntry is not null && templateFolder is not null)
        {
            await CollectTemplateChildrenAsync(
                templateFolder: templateFolder,
                packageRoot: packageRoot,
                resolverIndex: index,
                output: childArtefacts,
                ct: ct).ConfigureAwait(false);
        }
        else if (reference.IsCrossPackage && _catalogProvider is not null)
        {
            await CollectCrossPackageTemplateChildrenAsync(
                packageName: reference.PackageName!,
                parentKind: consumerKind,
                parentArtefactName: reference.ArtefactName,
                resolverIndex: index,
                packageRoot: packageRoot,
                output: childArtefacts,
                ct: ct).ConfigureAwait(false);
        }

        var combined = new List<ResolvedArtefact>(chainedChildren.Count + childArtefacts.Count);
        combined.AddRange(chainedChildren);
        combined.AddRange(childArtefacts);

        return (mergedYaml, combined);
    }

    /// <summary>
    /// Walks the template's folder tree and emits one
    /// <see cref="ResolvedArtefact"/> per concrete nested child
    /// (anything under <c>agents/</c> / <c>units/</c> with kind
    /// <c>Agent</c> or <c>Unit</c>; nested templates are themselves
    /// candidates for future cloning but don't produce activated rows).
    /// </summary>
    private async Task CollectTemplateChildrenAsync(
        string templateFolder,
        string? packageRoot,
        IReadOnlyList<TemplateIndexEntry> resolverIndex,
        List<ResolvedArtefact> output,
        CancellationToken ct)
    {
        if (!Directory.Exists(templateFolder))
        {
            return;
        }

        foreach (var subdirName in new[] { "agents", "units" })
        {
            ct.ThrowIfCancellationRequested();
            var subdirPath = Path.Combine(templateFolder, subdirName);
            if (!Directory.Exists(subdirPath))
            {
                continue;
            }

            foreach (var childDir in Directory.EnumerateDirectories(subdirPath))
            {
                ct.ThrowIfCancellationRequested();
                var manifestPath = Path.Combine(childDir, "package.yaml");
                if (!File.Exists(manifestPath))
                {
                    var alt = Path.Combine(childDir, "package.yml");
                    if (File.Exists(alt))
                    {
                        manifestPath = alt;
                    }
                    else
                    {
                        continue;
                    }
                }

                var rawYaml = File.ReadAllText(manifestPath);
                var (declaredKind, name, fromRef) = ReadIndexHeaders(rawYaml);
                if (string.IsNullOrWhiteSpace(declaredKind) || string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                // Concrete kinds only — nested templates don't activate.
                ArtefactKind childKind;
                if (string.Equals(declaredKind, "Unit", StringComparison.Ordinal))
                {
                    childKind = ArtefactKind.Unit;
                }
                else if (string.Equals(declaredKind, "Agent", StringComparison.Ordinal))
                {
                    childKind = ArtefactKind.Agent;
                }
                else
                {
                    continue;
                }

                string childYaml = rawYaml;

                // Recurse on the cloned child's own `from:` chain.
                IReadOnlyList<ResolvedArtefact> grandchildren = Array.Empty<ResolvedArtefact>();
                if (!string.IsNullOrWhiteSpace(fromRef))
                {
                    var (mergedYaml, gc) = await StampFromChainAsync(
                        consumerYaml: rawYaml,
                        consumerKind: childKind,
                        fromRef: fromRef!,
                        index: resolverIndex,
                        packageRoot: packageRoot,
                        visited: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                        ct: ct).ConfigureAwait(false);
                    childYaml = mergedYaml;
                    grandchildren = gc;
                }

                output.Add(new ResolvedArtefact
                {
                    Name = name!,
                    SourcePackage = null,
                    Kind = childKind,
                    ResolvedPath = manifestPath,
                    Content = childYaml,
                });
                output.AddRange(grandchildren);

                // Recurse on the cloned child's own subtree so a nested
                // concrete child of the cloned child also lands in the
                // output.
                await CollectTemplateChildrenAsync(
                    templateFolder: childDir,
                    packageRoot: packageRoot,
                    resolverIndex: resolverIndex,
                    output: output,
                    ct: ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Cross-package counterpart of <see cref="CollectTemplateChildrenAsync"/>:
    /// for a cross-package <c>from:</c> reference, asks the catalog
    /// provider for the source template's nested concrete children and
    /// stamps each one into the consumer's tree. ADR-0043 §5h
    /// archetype-library case.
    /// </summary>
    /// <remarks>
    /// Each cloned child's <see cref="ResolvedArtefact.SourcePackage"/>
    /// is left <c>null</c> — the child belongs to the consumer's
    /// package, not to the archetype library. Identity (a fresh Guid per
    /// ADR-0036) is minted by the install pipeline downstream. The
    /// resolver also recurses on each child's own <c>from:</c> chain so
    /// a cross-package archetype that itself chains into another
    /// archetype resolves transitively.
    /// </remarks>
    private async Task CollectCrossPackageTemplateChildrenAsync(
        string packageName,
        ArtefactKind parentKind,
        string parentArtefactName,
        IReadOnlyList<TemplateIndexEntry> resolverIndex,
        string? packageRoot,
        List<ResolvedArtefact> output,
        CancellationToken ct)
    {
        if (_catalogProvider is null)
        {
            return;
        }

        IReadOnlyList<NestedArtefactDescriptor> descriptors;
        try
        {
            descriptors = await _catalogProvider
                .EnumerateNestedArtefactsAsync(packageName, parentKind, parentArtefactName, ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // Defensive: a catalog provider that throws on the enumeration
            // shouldn't crash the resolver; degrade to "no nested children"
            // (cross-package archetypes that don't surface nested children
            // are still valid — they just don't stamp out a subtree).
            return;
        }

        foreach (var descriptor in descriptors)
        {
            ct.ThrowIfCancellationRequested();

            string childYaml = descriptor.Yaml;
            IReadOnlyList<ResolvedArtefact> grandchildren = Array.Empty<ResolvedArtefact>();

            // Recurse on the cloned child's own `from:` chain — handles
            // a cross-package archetype whose nested children themselves
            // chain across package boundaries.
            var fromRef = ReadFromField(descriptor.Yaml);
            if (!string.IsNullOrWhiteSpace(fromRef))
            {
                var (mergedYaml, gc) = await StampFromChainAsync(
                    consumerYaml: descriptor.Yaml,
                    consumerKind: descriptor.Kind,
                    fromRef: fromRef!,
                    index: resolverIndex,
                    packageRoot: packageRoot,
                    visited: new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    ct: ct).ConfigureAwait(false);
                childYaml = mergedYaml;
                grandchildren = gc;
            }

            output.Add(new ResolvedArtefact
            {
                Name = descriptor.Name,
                SourcePackage = null,
                Kind = descriptor.Kind,
                // No on-disk path — the child was materialised from the
                // catalog provider, not walked from the consumer's tree.
                ResolvedPath = null,
                Content = childYaml,
                ContainingArtefactName = descriptor.ContainingArtefactName,
            });
            output.AddRange(grandchildren);
        }
    }

    // ── YAML merge ───────────────────────────────────────────────────────

    /// <summary>
    /// Merges <paramref name="templateYaml"/> into
    /// <paramref name="consumerYaml"/> under the ADR-0043 §5d rules:
    /// scalars — consumer wins; maps — deep-merge with consumer keys
    /// winning; lists — consumer's list (when present) replaces
    /// template's. <c>kind:</c>, <c>name:</c>, <c>from:</c>, and
    /// <c>apiVersion:</c> are reserved keys whose consumer values always
    /// win.
    /// </summary>
    internal static string MergeYaml(string consumerYaml, string templateYaml)
    {
        var consumerDoc = ParseDoc(consumerYaml);
        var templateDoc = ParseDoc(templateYaml);

        if (consumerDoc is null)
        {
            return templateYaml;
        }
        if (templateDoc is null)
        {
            return consumerYaml;
        }

        var merged = MergeMapping(consumer: consumerDoc, template: templateDoc);
        return Serialise(merged);
    }

    private static YamlMappingNode? ParseDoc(string yamlText)
    {
        if (string.IsNullOrWhiteSpace(yamlText))
        {
            return null;
        }
        var stream = new YamlStream();
        using var reader = new StringReader(yamlText);
        stream.Load(reader);
        if (stream.Documents.Count == 0)
        {
            return null;
        }
        return stream.Documents[0].RootNode as YamlMappingNode;
    }

    private static YamlMappingNode MergeMapping(YamlMappingNode consumer, YamlMappingNode template)
    {
        // ADR-0043 §5d:
        //   * scalars → consumer wins (template fills when consumer is absent)
        //   * maps    → deep-merge per key; consumer keys win at every level
        //   * lists   → consumer's list (when present) replaces template's
        // Reserved keys (kind, name, from, apiVersion) → consumer wins.
        var output = new YamlMappingNode();

        // Copy every consumer key first so the iteration order matches
        // the author's intent for the body fields they wrote.
        foreach (var (k, v) in consumer.Children)
        {
            output.Children[CloneNode(k)] = CloneNode(v);
        }

        foreach (var (k, v) in template.Children)
        {
            var keyName = (k as YamlScalarNode)?.Value;
            if (keyName is null)
            {
                continue;
            }

            // Reserved keys — consumer always wins. Skip the template's
            // value when the consumer declared one.
            if (IsReservedKey(keyName))
            {
                if (!output.Children.ContainsKey(k))
                {
                    output.Children[CloneNode(k)] = CloneNode(v);
                }
                continue;
            }

            if (!output.Children.TryGetValue(k, out var consumerValue))
            {
                // Consumer didn't declare this key — template flows through.
                output.Children[CloneNode(k)] = CloneNode(v);
                continue;
            }

            // Both sides declared the key. Dispatch on the value's shape.
            if (consumerValue is YamlMappingNode consumerMap && v is YamlMappingNode templateMap)
            {
                // Deep-merge.
                output.Children[CloneNode(k)] = MergeMapping(consumerMap, templateMap);
                continue;
            }

            if (consumerValue is YamlSequenceNode)
            {
                // Lists — consumer's list replaces; already in `output`.
                continue;
            }

            // Scalars — consumer wins; already in `output`.
        }

        return output;
    }

    private static bool IsReservedKey(string keyName) =>
        string.Equals(keyName, "kind", StringComparison.Ordinal)
        || string.Equals(keyName, "name", StringComparison.Ordinal)
        || string.Equals(keyName, "from", StringComparison.Ordinal)
        || string.Equals(keyName, "apiVersion", StringComparison.Ordinal);

    private static YamlNode CloneNode(YamlNode node) => node switch
    {
        YamlScalarNode s => new YamlScalarNode(s.Value) { Style = s.Style, Tag = s.Tag },
        YamlMappingNode m => CloneMapping(m),
        YamlSequenceNode seq => CloneSequence(seq),
        _ => node,
    };

    private static YamlMappingNode CloneMapping(YamlMappingNode source)
    {
        var clone = new YamlMappingNode();
        foreach (var (k, v) in source.Children)
        {
            clone.Children[CloneNode(k)] = CloneNode(v);
        }
        return clone;
    }

    private static YamlSequenceNode CloneSequence(YamlSequenceNode source)
    {
        var clone = new YamlSequenceNode();
        foreach (var child in source.Children)
        {
            clone.Add(CloneNode(child));
        }
        return clone;
    }

    private static string Serialise(YamlMappingNode root)
    {
        var stream = new YamlStream(new YamlDocument(root));
        using var writer = new StringWriter();
        stream.Save(writer, assignAnchors: false);
        var text = writer.ToString();
        // YamlStream.Save writes a trailing `...` end-of-document marker.
        // Strip it so the output stays the byte-stable shape downstream
        // parsers expect.
        var trimmedEnd = text.TrimEnd();
        if (trimmedEnd.EndsWith("...", StringComparison.Ordinal))
        {
            trimmedEnd = trimmedEnd[..^3].TrimEnd();
        }
        return trimmedEnd + "\n";
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static string? ReadFromField(string yamlText)
    {
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            var headers = deserializer.Deserialize<FromOnly>(yamlText);
            return headers?.From;
        }
        catch
        {
            return null;
        }
    }

    private sealed class FromOnly
    {
        [YamlMember(Alias = "from")]
        public string? From { get; set; }
    }
}
