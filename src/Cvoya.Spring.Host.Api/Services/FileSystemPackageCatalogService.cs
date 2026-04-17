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

using YamlDotNet.RepresentationModel;

/// <summary>
/// File-system backed <see cref="IPackageCatalogService"/>. Scans a
/// <c>packages/</c> root on disk and materialises summary + detail
/// responses for every <c>packages/{package}/...</c> directory.
///
/// The packages root is configured via <see cref="PackageCatalogOptions.Root"/>
/// (setting <c>Packages:Root</c>). When the directory is missing the
/// service returns empty results rather than throwing — the normal case
/// for deployments that don't ship the packages tree alongside the API.
/// </summary>
/// <remarks>
/// Agent manifests ship under <c>agents/</c> with an <c>agent:</c> root
/// key (not the unit grammar <see cref="ManifestParser"/> targets), so
/// they're parsed via the lower-level YamlDotNet representation model to
/// pluck display metadata without coupling this service to a second
/// typed manifest. The skill-bundle detection mirrors the convention
/// documented in <c>docs/architecture/packages.md</c> (§ Authoring a
/// Skill Bundle) — <c>{name}.md</c> is the bundle, <c>{name}.tools.json</c>
/// is an optional sibling.
/// </remarks>
public class FileSystemPackageCatalogService(
    PackageCatalogOptions options,
    ILogger<FileSystemPackageCatalogService> logger)
    : IPackageCatalogService
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
            packages.Add(new PackageSummary(
                Name: name,
                Description: TryReadReadmeSummary(packageDir),
                UnitTemplateCount: CountManifestFiles(Path.Combine(packageDir, "units")),
                AgentTemplateCount: CountManifestFiles(Path.Combine(packageDir, "agents")),
                SkillCount: CountSkillBundles(Path.Combine(packageDir, "skills")),
                ConnectorCount: CountAssets(Path.Combine(packageDir, "connectors")),
                WorkflowCount: CountDirectories(Path.Combine(packageDir, "workflows"))));
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

        // Defensive: reject identifiers that would escape the packages
        // root. Mirrors the guard on LoadUnitTemplateYamlAsync — the
        // browse surface is Viewer-gated and the catalog lives on disk,
        // so path traversal would otherwise hand an attacker an arbitrary
        // directory listing.
        if (ContainsTraversal(name))
        {
            return Task.FromResult<PackageDetail?>(null);
        }

        var packageDir = Path.Combine(root, name);
        if (!Directory.Exists(packageDir))
        {
            return Task.FromResult<PackageDetail?>(null);
        }

        var unitTemplates = ReadUnitTemplates(packageDir, name, cancellationToken);
        var agentTemplates = ReadAgentTemplates(packageDir, name, cancellationToken);
        var skills = ReadSkills(packageDir, name, cancellationToken);
        var connectors = ReadConnectors(packageDir, name, cancellationToken);
        var workflows = ReadWorkflows(packageDir, name, cancellationToken);

        var detail = new PackageDetail(
            Name: name,
            Description: TryReadReadmeSummary(packageDir),
            UnitTemplates: unitTemplates,
            AgentTemplates: agentTemplates,
            Skills: skills,
            Connectors: connectors,
            Workflows: workflows);

        return Task.FromResult<PackageDetail?>(detail);
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
            templates.AddRange(ReadUnitTemplates(packageDir, packageName, cancellationToken));
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

        // Defensive: reject identifiers that attempt directory traversal so the
        // caller can't read arbitrary files off the host by asking for
        // "../../etc/passwd" as a template name.
        if (ContainsTraversal(package) || ContainsTraversal(name))
        {
            return null;
        }

        var candidate = Path.Combine(root, package, "units", name + ".yaml");
        if (!File.Exists(candidate))
        {
            candidate = Path.Combine(root, package, "units", name + ".yml");
            if (!File.Exists(candidate))
            {
                return null;
            }
        }

        // Re-check the resolved path is still inside the packages root.
        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.StartsWith(fullRoot, StringComparison.Ordinal))
        {
            return null;
        }

        return await File.ReadAllTextAsync(fullCandidate, cancellationToken);
    }

    private List<UnitTemplateSummary> ReadUnitTemplates(
        string packageDir,
        string packageName,
        CancellationToken cancellationToken)
    {
        var result = new List<UnitTemplateSummary>();
        var unitsDir = Path.Combine(packageDir, "units");
        if (!Directory.Exists(unitsDir))
        {
            return result;
        }

        foreach (var file in EnumerateYamlFiles(unitsDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var yaml = File.ReadAllText(file);
                var manifest = ManifestParser.Parse(yaml);
                result.Add(new UnitTemplateSummary(
                    Package: packageName,
                    Name: manifest.Name!,
                    Description: manifest.Description,
                    Path: RelativePath(file)));
            }
            catch (ManifestParseException ex)
            {
                logger.LogWarning(
                    ex,
                    "Skipping unit template '{File}' because its YAML could not be parsed.",
                    file);
            }
        }

        result.Sort(static (a, b) =>
            string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private List<AgentTemplateSummary> ReadAgentTemplates(
        string packageDir,
        string packageName,
        CancellationToken cancellationToken)
    {
        var result = new List<AgentTemplateSummary>();
        var agentsDir = Path.Combine(packageDir, "agents");
        if (!Directory.Exists(agentsDir))
        {
            return result;
        }

        foreach (var file in EnumerateYamlFiles(agentsDir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var agent = ReadAgentManifest(file);
                // Fall back to the file basename when the manifest omits
                // the id field so every agent YAML still appears in the
                // catalog rather than silently dropping.
                var id = agent.Id ?? Path.GetFileNameWithoutExtension(file);
                result.Add(new AgentTemplateSummary(
                    Package: packageName,
                    Name: id,
                    DisplayName: agent.Name,
                    Role: agent.Role,
                    Description: Truncate(agent.Instructions, maxLength: 240),
                    Path: RelativePath(file)));
            }
            catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or IOException)
            {
                logger.LogWarning(
                    ex,
                    "Skipping agent template '{File}' because its YAML could not be parsed.",
                    file);
            }
        }

        result.Sort(static (a, b) =>
            string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private List<SkillSummary> ReadSkills(
        string packageDir,
        string packageName,
        CancellationToken cancellationToken)
    {
        var result = new List<SkillSummary>();
        var skillsDir = Path.Combine(packageDir, "skills");
        if (!Directory.Exists(skillsDir))
        {
            return result;
        }

        foreach (var file in Directory.EnumerateFiles(skillsDir, "*.md"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileNameWithoutExtension(file);
            var toolsFile = Path.Combine(skillsDir, name + ".tools.json");
            result.Add(new SkillSummary(
                Package: packageName,
                Name: name,
                HasTools: File.Exists(toolsFile),
                Path: RelativePath(file)));
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

    private List<WorkflowSummary> ReadWorkflows(
        string packageDir,
        string packageName,
        CancellationToken cancellationToken)
    {
        var result = new List<WorkflowSummary>();
        var dir = Path.Combine(packageDir, "workflows");
        if (!Directory.Exists(dir))
        {
            return result;
        }

        foreach (var entry in Directory.EnumerateDirectories(dir))
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Add(new WorkflowSummary(
                Package: packageName,
                Name: Path.GetFileName(entry),
                Path: RelativePath(entry)));
        }

        result.Sort(static (a, b) =>
            string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return result;
    }

    private string RelativePath(string absolutePath)
        => Path.GetRelativePath(options.Root!, absolutePath).Replace('\\', '/');

    private static IEnumerable<string> EnumerateYamlFiles(string directory)
        => Directory.EnumerateFiles(directory, "*.yaml")
            .Concat(Directory.EnumerateFiles(directory, "*.yml"));

    private static int CountManifestFiles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }
        return EnumerateYamlFiles(directory).Count();
    }

    private static int CountSkillBundles(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }
        return Directory.EnumerateFiles(directory, "*.md").Count();
    }

    private static int CountAssets(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }
        return Directory.EnumerateFileSystemEntries(directory).Count();
    }

    private static int CountDirectories(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return 0;
        }
        return Directory.EnumerateDirectories(directory).Count();
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
            // Pluck the first non-empty paragraph that isn't a heading. The
            // README layout isn't enforced, so this is best-effort: a null
            // is better than surfacing noisy markdown to the cards.
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
            // Readme is advisory metadata — a read failure should not
            // prevent the package from appearing in the catalog.
        }

        return null;
    }

    private static AgentManifestView ReadAgentManifest(string file)
    {
        using var reader = new StreamReader(file);
        var stream = new YamlStream();
        stream.Load(reader);

        if (stream.Documents.Count == 0
            || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            return default;
        }

        if (!root.Children.TryGetValue(new YamlScalarNode("agent"), out var agentNode)
            || agentNode is not YamlMappingNode agent)
        {
            return default;
        }

        return new AgentManifestView(
            Id: TryReadScalar(agent, "id"),
            Name: TryReadScalar(agent, "name"),
            Role: TryReadScalar(agent, "role"),
            Instructions: TryReadScalar(agent, "instructions"));
    }

    private static string? TryReadScalar(YamlMappingNode node, string key)
    {
        if (node.Children.TryGetValue(new YamlScalarNode(key), out var value)
            && value is YamlScalarNode scalar)
        {
            return string.IsNullOrWhiteSpace(scalar.Value) ? null : scalar.Value;
        }
        return null;
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

    /// <summary>
    /// Minimal internal view over the <c>agent:</c> YAML node. We keep
    /// this internal so packages.md's authoring contract stays the
    /// external surface — the catalog only needs a few display fields.
    /// </summary>
    private readonly record struct AgentManifestView(
        string? Id,
        string? Name,
        string? Role,
        string? Instructions);
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