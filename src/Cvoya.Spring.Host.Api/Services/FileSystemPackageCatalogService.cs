// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Host.Api.Models;
using Cvoya.Spring.Manifest;

using Microsoft.Extensions.Logging;

using ArtefactKind = Cvoya.Spring.Manifest.ArtefactKind;

/// <summary>
/// File-system backed <see cref="IPackageCatalogService"/>. Scans a
/// <c>packages/</c> root on disk and materialises summary + detail
/// responses for every <c>packages/{package}/</c> directory following the
/// ADR-0043 recursive folder layout: each artefact is a folder rooted at
/// <c>package.yaml</c>; conventional subdirectories (<c>units/</c>,
/// <c>agents/</c>, <c>skills/</c>, <c>workflows/</c>, <c>connectors/</c>,
/// <c>templates/</c>) compose recursively to any depth.
///
/// The packages root is configured via <see cref="PackageCatalogOptions.Root"/>
/// (setting <c>Packages:Root</c>). When the directory is missing the
/// service returns empty results rather than throwing — the normal case
/// for deployments that don't ship the packages tree alongside the API.
/// </summary>
public class FileSystemPackageCatalogService(
    PackageCatalogOptions options,
    ILogger<FileSystemPackageCatalogService> logger)
    : IPackageCatalogService, IPackageCatalogProvider
{
    /// <inheritdoc />
    public Task<IReadOnlyList<PackageSummary>> ListPackagesAsync(
        CancellationToken cancellationToken)
    {
        var root = options.Root;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            logger.LogDebug(
                "Package catalog root '{Root}' does not exist; returning empty package list.",
                root);
            return Task.FromResult<IReadOnlyList<PackageSummary>>(Array.Empty<PackageSummary>());
        }

        var packages = new List<PackageSummary>();
        foreach (var packageDir in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var name = Path.GetFileName(packageDir);
            var discovered = TryWalk(packageDir);
            var topLevel = discovered.Where(d => IsAtDepth(packageDir, d.FolderPath, depth: 2)).ToList();

            packages.Add(new PackageSummary(
                Name: name,
                Description: TryReadReadmeSummary(packageDir),
                UnitTemplateCount: topLevel.Count(d => d.Kind == ArtefactKind.Unit),
                AgentTemplateCount: topLevel.Count(d => d.Kind == ArtefactKind.Agent),
                SkillCount: topLevel.Count(d => d.Kind == ArtefactKind.Skill),
                ConnectorCount: CountAssets(Path.Combine(packageDir, "connectors")),
                WorkflowCount: topLevel.Count(d => d.Kind == ArtefactKind.Workflow),
                Version: ReadVersion(packageDir)));
        }

        packages.Sort(static (a, b) =>
            string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult<IReadOnlyList<PackageSummary>>(packages);
    }

    /// <inheritdoc />
    public Task<PackageDetail?> GetPackageAsync(
        string name,
        CancellationToken cancellationToken)
    {
        var root = options.Root;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return Task.FromResult<PackageDetail?>(null);
        }

        if (ContainsTraversal(name))
        {
            return Task.FromResult<PackageDetail?>(null);
        }

        var packageDir = Path.Combine(root, name);
        if (!Directory.Exists(packageDir))
        {
            return Task.FromResult<PackageDetail?>(null);
        }

        var discovered = TryWalk(packageDir, cancellationToken);

        var unitTemplates = BuildUnitTemplateSummaries(discovered, name);
        var agentTemplates = BuildAgentTemplateSummaries(discovered, name);
        var skills = BuildSkillSummaries(discovered, name);
        var connectors = ReadConnectors(packageDir, name, cancellationToken);
        var workflows = BuildWorkflowSummaries(discovered, name);

        var connectorDeclarations = ReadConnectorDeclarations(discovered);
        var version = ReadVersion(packageDir);
        var execution = ReadPackageExecution(packageDir);

        var detail = new PackageDetail(
            Name: name,
            Description: TryReadReadmeSummary(packageDir),
            Readme: TryReadReadmeFull(packageDir),
            Version: version,
            UnitTemplates: unitTemplates,
            AgentTemplates: agentTemplates,
            Skills: skills,
            Connectors: connectors,
            Workflows: workflows,
            ConnectorDeclarations: connectorDeclarations,
            Content: BuildContentSummary(discovered, packageDir),
            Execution: execution);

        return Task.FromResult<PackageDetail?>(detail);
    }

    /// <summary>
    /// Reads the package manifest's top-level <c>version:</c> scalar.
    /// </summary>
    private string? ReadVersion(string packageDir)
    {
        var manifestPath = FindManifestPath(packageDir);
        if (manifestPath is null) return null;

        try
        {
            var yaml = File.ReadAllText(manifestPath);
            var manifest = PackageManifestParser.ParseRaw(yaml);
            return manifest.Version;
        }
        catch (Exception ex) when (ex is PackageParseException or YamlDotNet.Core.YamlException or IOException)
        {
            logger.LogDebug(ex,
                "Skipping version for package manifest '{Path}' because it could not be parsed.",
                manifestPath);
            return null;
        }
    }

    /// <summary>
    /// Reads the package manifest's <c>execution:</c> block.
    /// </summary>
    private PackageExecutionSummary? ReadPackageExecution(string packageDir)
    {
        var manifestPath = FindManifestPath(packageDir);
        if (manifestPath is null) return null;

        try
        {
            var yaml = File.ReadAllText(manifestPath);
            var resolved = PackageManifestParser.ParseAndResolveAsync(
                yaml, packageDir).GetAwaiter().GetResult();
            var exec = resolved.Execution;
            if (exec is null)
            {
                return null;
            }
            return new PackageExecutionSummary(
                Image: exec.Image,
                Provider: exec.Provider,
                Model: exec.Model,
                InheritUnits: exec.InheritUnits);
        }
        catch (Exception ex) when (ex is PackageParseException or YamlDotNet.Core.YamlException or IOException)
        {
            logger.LogDebug(ex,
                "Skipping execution declaration for package manifest '{Path}' because it could not be parsed.",
                manifestPath);
            return null;
        }
    }

    /// <summary>
    /// Builds the <c>content:</c>-style top-level summary used by the
    /// portal / CLI display. Under ADR-0043 there is no <c>content:</c>
    /// list in YAML — top-level artefacts are the folders directly under
    /// the package root's conventional subdirectories. We surface that
    /// same shape so the CLI's "what gets installed" view keeps working.
    /// </summary>
    private List<PackageContentEntry> BuildContentSummary(
        IReadOnlyList<DiscoveredEntry> discovered,
        string packageDir)
    {
        var result = new List<PackageContentEntry>();
        foreach (var d in discovered)
        {
            // Only top-level artefacts go into the content summary —
            // nested artefacts are owned by their containing artefact.
            if (!IsAtDepth(packageDir, d.FolderPath, depth: 2))
            {
                continue;
            }
            var key = d.Kind switch
            {
                ArtefactKind.Unit => "unit",
                ArtefactKind.Agent => "agent",
                ArtefactKind.Skill => "skill",
                ArtefactKind.Workflow => "workflow",
                _ => d.Kind.ToString().ToLowerInvariant(),
            };
            result.Add(new PackageContentEntry(key, d.Name));
        }
        return result;
    }

    private List<RequiredConnectorSummary> ReadConnectorDeclarations(
        IReadOnlyList<DiscoveredEntry> discovered)
    {
        var union = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var d in discovered)
            {
                if (d.Kind != ArtefactKind.Unit && d.Kind != ArtefactKind.Agent)
                {
                    continue;
                }
                var slugs = ExtractConnectorSlugs(d);
                foreach (var s in slugs) union.Add(s);
            }
        }
        catch (Exception ex) when (ex is PackageParseException or YamlDotNet.Core.YamlException or IOException)
        {
            logger.LogWarning(ex,
                "Skipping connector declarations because an artefact YAML could not be parsed.");
            return [];
        }

        var result = new List<RequiredConnectorSummary>(union.Count);
        foreach (var slug in union)
        {
            result.Add(new RequiredConnectorSummary(Type: slug, Required: true));
        }
        return result;
    }

    private static IEnumerable<string> ExtractConnectorSlugs(DiscoveredEntry entry)
    {
        var yaml = entry.RawYaml;
        if (string.IsNullOrWhiteSpace(yaml)) yield break;

        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new RequirementEntryYamlConverter())
            .IgnoreUnmatchedProperties()
            .Build();

        List<RequirementEntry>? requires = null;
        try
        {
            if (entry.Kind == ArtefactKind.Unit)
            {
                requires = deserializer.Deserialize<UnitManifest>(yaml)?.Requires;
            }
            else if (entry.Kind == ArtefactKind.Agent)
            {
                requires = deserializer.Deserialize<AgentManifest>(yaml)?.Requires;
            }
        }
        catch (YamlDotNet.Core.YamlException)
        {
            yield break;
        }

        if (requires is null) yield break;
        foreach (var r in requires)
        {
            if (r.Type != RequirementType.Connector) continue;
            var slug = r.Identifier?.Trim();
            if (string.IsNullOrEmpty(slug)) continue;
            yield return slug;
        }
    }

    private static string? FindManifestPath(string packageDir)
    {
        foreach (var ext in new[] { "package.yaml", "package.yml" })
        {
            var candidate = Path.Combine(packageDir, ext);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<UnitTemplateSummary>> ListUnitTemplatesAsync(
        CancellationToken cancellationToken)
    {
        var root = options.Root;
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            logger.LogDebug(
                "Package catalog root '{Root}' does not exist; returning empty template list.",
                root);
            return Task.FromResult<IReadOnlyList<UnitTemplateSummary>>(Array.Empty<UnitTemplateSummary>());
        }

        var templates = new List<UnitTemplateSummary>();
        foreach (var packageDir in Directory.EnumerateDirectories(root))
        {
            var packageName = Path.GetFileName(packageDir);
            var discovered = TryWalk(packageDir, cancellationToken);
            templates.AddRange(BuildUnitTemplateSummaries(discovered, packageName));
        }

        templates.Sort(static (a, b) =>
        {
            var cmp = string.Compare(a.Package, b.Package, StringComparison.OrdinalIgnoreCase);
            return cmp != 0 ? cmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        });

        return Task.FromResult<IReadOnlyList<UnitTemplateSummary>>(templates);
    }

    /// <inheritdoc />
    public async Task<string?> LoadUnitTemplateYamlAsync(
        string package,
        string name,
        CancellationToken cancellationToken)
    {
        var root = options.Root;
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        if (ContainsTraversal(package) || ContainsTraversal(name))
        {
            return null;
        }

        var packageDir = Path.Combine(root, package);
        if (!Directory.Exists(packageDir))
        {
            return null;
        }

        // Find the unit's folder by walking the package. The unit may
        // live at any depth under `units/`; we resolve by name.
        var discovered = TryWalk(packageDir, cancellationToken);
        var match = discovered.FirstOrDefault(
            d => d.Kind == ArtefactKind.Unit &&
                 string.Equals(d.Name, name, StringComparison.Ordinal));
        if (match is null)
        {
            return null;
        }

        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(match.PackageYamlPath);
        if (!fullCandidate.StartsWith(fullRoot, StringComparison.Ordinal))
        {
            return null;
        }

        return await File.ReadAllTextAsync(fullCandidate, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task<bool> PackageExistsAsync(string packageName, CancellationToken cancellationToken = default)
    {
        var root = options.Root;
        if (string.IsNullOrWhiteSpace(root) || ContainsTraversal(packageName))
        {
            return Task.FromResult(false);
        }

        var packageDir = Path.Combine(root, packageName);
        if (!Directory.Exists(packageDir))
        {
            return Task.FromResult(false);
        }

        var fullRoot = Path.GetFullPath(root);
        var fullPackageDir = Path.GetFullPath(packageDir);
        return Task.FromResult(fullPackageDir.StartsWith(fullRoot, StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public async Task<string?> LoadPackageManifestYamlAsync(
        string packageName,
        CancellationToken cancellationToken)
    {
        var root = options.Root;
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        if (ContainsTraversal(packageName))
        {
            return null;
        }

        var packageDir = Path.Combine(root, packageName);
        if (!Directory.Exists(packageDir))
        {
            return null;
        }

        var fullRoot = Path.GetFullPath(root);
        var fullPackageDir = Path.GetFullPath(packageDir);
        if (!fullPackageDir.StartsWith(fullRoot, StringComparison.Ordinal))
        {
            return null;
        }

        foreach (var ext in new[] { "package.yaml", "package.yml" })
        {
            var candidate = Path.Combine(fullPackageDir, ext);
            if (File.Exists(candidate))
            {
                return await File.ReadAllTextAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
        }

        return null;
    }

    /// <inheritdoc />
    public async Task<string?> LoadArtefactYamlAsync(
        string packageName,
        ArtefactKind kind,
        string artefactName,
        CancellationToken cancellationToken)
    {
        var root = options.Root;
        if (string.IsNullOrWhiteSpace(root))
        {
            return null;
        }

        if (ContainsTraversal(packageName) || ContainsTraversal(artefactName))
        {
            return null;
        }

        var packageDir = Path.Combine(root, packageName);
        if (!Directory.Exists(packageDir))
        {
            return null;
        }

        var discovered = TryWalk(packageDir, cancellationToken);
        var match = discovered.FirstOrDefault(
            d => d.Kind == kind &&
                 string.Equals(d.Name, artefactName, StringComparison.Ordinal));
        if (match is null)
        {
            return null;
        }

        // For skill folders, the companion markdown is the canonical
        // body the platform consumes (compatibility with the pre-ADR-0043
        // single-file shape). When present, return that. Otherwise return
        // the package.yaml so callers see at least the headers.
        if (kind == ArtefactKind.Skill)
        {
            var md = Path.Combine(match.FolderPath, match.Name + ".md");
            if (File.Exists(md))
            {
                var fullMd = Path.GetFullPath(md);
                var fullRootForMd = Path.GetFullPath(root);
                if (fullMd.StartsWith(fullRootForMd, StringComparison.Ordinal))
                {
                    return await File.ReadAllTextAsync(fullMd, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(match.PackageYamlPath);
        if (!fullCandidate.StartsWith(fullRoot, StringComparison.Ordinal))
        {
            return null;
        }

        return await File.ReadAllTextAsync(fullCandidate, cancellationToken).ConfigureAwait(false);
    }

    // ── Walker glue ─────────────────────────────────────────────────────

    /// <summary>
    /// Lightweight wrapper around <see cref="PackageManifestParser.Walk"/>
    /// that also caches the raw YAML of each discovered artefact so the
    /// requires-union / display-metadata reads don't have to re-read each
    /// file from disk. Empty when the walker raises any
    /// <see cref="PackageParseException"/> (best-effort catalog read; the
    /// install pipeline will surface the same error with the full message).
    /// </summary>
    private IReadOnlyList<DiscoveredEntry> TryWalk(
        string packageDir, CancellationToken cancellationToken = default)
    {
        try
        {
            var walked = PackageManifestParser.Walk(packageDir, cancellationToken);
            var result = new List<DiscoveredEntry>(walked.Count);
            foreach (var (kind, name, folderPath) in walked)
            {
                var manifestPath = Path.Combine(folderPath, "package.yaml");
                if (!File.Exists(manifestPath))
                {
                    var alt = Path.Combine(folderPath, "package.yml");
                    if (File.Exists(alt)) manifestPath = alt;
                }
                string rawYaml;
                try
                {
                    rawYaml = File.ReadAllText(manifestPath);
                }
                catch (IOException)
                {
                    rawYaml = string.Empty;
                }
                result.Add(new DiscoveredEntry(kind, name, folderPath, manifestPath, rawYaml));
            }
            return result;
        }
        catch (PackageParseException ex)
        {
            logger.LogDebug(ex,
                "Catalog walker rejected package at '{Dir}'; returning empty discovery list.",
                packageDir);
            return Array.Empty<DiscoveredEntry>();
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex,
                "Catalog walker encountered an IO error on package '{Dir}'; returning empty discovery list.",
                packageDir);
            return Array.Empty<DiscoveredEntry>();
        }
    }

    private sealed record DiscoveredEntry(
        ArtefactKind Kind,
        string Name,
        string FolderPath,
        string PackageYamlPath,
        string RawYaml);

    private List<UnitTemplateSummary> BuildUnitTemplateSummaries(
        IReadOnlyList<DiscoveredEntry> discovered,
        string packageName)
    {
        var result = new List<UnitTemplateSummary>();
        foreach (var d in discovered)
        {
            if (d.Kind != ArtefactKind.Unit) continue;
            try
            {
                var manifest = ManifestParser.Parse(d.RawYaml);
                result.Add(new UnitTemplateSummary(
                    Package: packageName,
                    Name: manifest.Name!,
                    Description: manifest.Description,
                    Path: RelativePath(d.PackageYamlPath)));
            }
            catch (ManifestParseException ex)
            {
                logger.LogWarning(
                    ex,
                    "Skipping unit template '{Path}' because its YAML could not be parsed.",
                    d.PackageYamlPath);
            }
        }
        result.Sort(static (a, b) =>
            string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private List<AgentTemplateSummary> BuildAgentTemplateSummaries(
        IReadOnlyList<DiscoveredEntry> discovered,
        string packageName)
    {
        var result = new List<AgentTemplateSummary>();
        var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
            .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
        foreach (var d in discovered)
        {
            if (d.Kind != ArtefactKind.Agent) continue;
            try
            {
                var agent = deserializer.Deserialize<AgentManifest>(d.RawYaml);
                if (agent is null) continue;
                var id = agent.Id ?? agent.Name ?? d.Name;
                result.Add(new AgentTemplateSummary(
                    Package: packageName,
                    Name: id,
                    DisplayName: agent.Name,
                    Role: agent.Role,
                    Description: Truncate(agent.Instructions, maxLength: 240),
                    Path: RelativePath(d.PackageYamlPath)));
            }
            catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or IOException)
            {
                logger.LogWarning(
                    ex,
                    "Skipping agent template '{Path}' because its YAML could not be parsed.",
                    d.PackageYamlPath);
            }
        }
        result.Sort(static (a, b) =>
            string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private List<SkillSummary> BuildSkillSummaries(
        IReadOnlyList<DiscoveredEntry> discovered,
        string packageName)
    {
        var result = new List<SkillSummary>();
        foreach (var d in discovered)
        {
            if (d.Kind != ArtefactKind.Skill) continue;
            // Companion markdown body lives at `<folder>/<name>.md` in
            // the recursive shape; an adjacent tools.json is optional.
            var mdPath = Path.Combine(d.FolderPath, d.Name + ".md");
            var hasMd = File.Exists(mdPath);
            var toolsPath = Path.Combine(d.FolderPath, d.Name + ".tools.json");
            result.Add(new SkillSummary(
                Package: packageName,
                Name: d.Name,
                HasTools: File.Exists(toolsPath),
                Path: RelativePath(hasMd ? mdPath : d.PackageYamlPath)));
        }
        result.Sort(static (a, b) =>
            string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private List<WorkflowSummary> BuildWorkflowSummaries(
        IReadOnlyList<DiscoveredEntry> discovered,
        string packageName)
    {
        var result = new List<WorkflowSummary>();
        foreach (var d in discovered)
        {
            if (d.Kind != ArtefactKind.Workflow) continue;
            result.Add(new WorkflowSummary(
                Package: packageName,
                Name: d.Name,
                Path: RelativePath(d.FolderPath)));
        }
        result.Sort(static (a, b) =>
            string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private List<ConnectorSummary> ReadConnectors(
        string packageDir,
        string packageName,
        CancellationToken cancellationToken)
    {
        // The `connectors/` directory is reserved per ADR-0037 §1 and
        // ADR-0043 §2 — concrete activation semantics ship with the
        // connector ADR. We surface whatever lives there so the portal
        // can show what's been authored.
        var result = new List<ConnectorSummary>();
        var dir = Path.Combine(packageDir, "connectors");
        if (!Directory.Exists(dir))
        {
            return result;
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new ConnectorSummary(
                Package: packageName,
                Name: Path.GetFileName(entry),
                Path: RelativePath(entry)));
        }
        result.Sort(static (a, b) =>
            string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    /// <summary>
    /// Returns true when <paramref name="path"/> is at exactly
    /// <paramref name="depth"/> segments below <paramref name="root"/>.
    /// Depth 2 = direct child of a top-level conventional subdir
    /// (e.g. <c>&lt;pkg&gt;/units/foo</c>).
    /// </summary>
    private static bool IsAtDepth(string root, string path, int depth)
    {
        var rel = Path.GetRelativePath(root, path);
        if (rel.StartsWith("..")) return false;
        var segments = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        return segments.Length == depth;
    }

    private string RelativePath(string absolutePath)
        => Path.GetRelativePath(options.Root!, absolutePath).Replace('\\', '/');

    private static int CountAssets(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }
        return Directory.EnumerateFileSystemEntries(directory).Count();
    }

    private static string? TryReadReadmeSummary(string packageDir)
    {
        var readme = Path.Combine(packageDir, "README.md");
        if (!File.Exists(readme))
        {
            return null;
        }

        try
        {
            foreach (var raw in File.ReadLines(readme))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }
                return Truncate(line, maxLength: 240);
            }
        }
        catch (IOException)
        {
            // README is advisory metadata.
        }

        return null;
    }

    private static string? TryReadReadmeFull(string packageDir)
    {
        var readme = Path.Combine(packageDir, "README.md");
        if (!File.Exists(readme))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(readme);
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var single = value.ReplaceLineEndings(" ").Trim();
        if (single.Length <= maxLength)
        {
            return single;
        }
        return single[..maxLength] + "…";
    }

    private static bool ContainsTraversal(string segment) =>
        string.IsNullOrWhiteSpace(segment)
        || segment.Contains("..", StringComparison.Ordinal)
        || segment.Contains('/', StringComparison.Ordinal)
        || segment.Contains('\\', StringComparison.Ordinal);
}

/// <summary>
/// Options bag for the file-system backed package catalog.
/// </summary>
public class PackageCatalogOptions
{
    /// <summary>
    /// Absolute or relative path to the packages root. When <c>null</c> or the
    /// path does not exist, the catalog is empty.
    /// </summary>
    public string? Root { get; set; }
}
