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

using ArtefactKind = Cvoya.Spring.Manifest.ArtefactKind;

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

        var inputs = ReadPackageInputs(packageDir);
        var unitTemplates = ReadUnitTemplates(packageDir, name, cancellationToken);
        var agentTemplates = ReadAgentTemplates(packageDir, name, cancellationToken);
        var skills = ReadSkills(packageDir, name, cancellationToken);
        var connectors = ReadConnectors(packageDir, name, cancellationToken);
        var workflows = ReadWorkflows(packageDir, name, cancellationToken);

        var connectorDeclarations = ReadConnectorDeclarations(packageDir);
        var content = ReadContentEntries(packageDir);

        var detail = new PackageDetail(
            Name: name,
            Description: TryReadReadmeSummary(packageDir),
            Readme: TryReadReadmeFull(packageDir),
            Inputs: inputs,
            UnitTemplates: unitTemplates,
            AgentTemplates: agentTemplates,
            Skills: skills,
            Connectors: connectors,
            Workflows: workflows,
            ConnectorDeclarations: connectorDeclarations,
            Content: content);

        return Task.FromResult<PackageDetail?>(detail);
    }

    /// <summary>
    /// Reads the package manifest's <c>content:</c> list (#1718 item 2) so
    /// the wizard / CLI can render "what this package installs" alongside
    /// the inputs and connector-declaration sections. Failures fall back
    /// to an empty list — best-effort metadata, like
    /// <see cref="ReadConnectorDeclarations"/> and
    /// <see cref="ReadPackageInputs"/>.
    /// </summary>
    private List<PackageContentEntry> ReadContentEntries(string packageDir)
    {
        var manifestPath = FindManifestPath(packageDir);
        if (manifestPath is null)
        {
            return [];
        }

        try
        {
            var yaml = File.ReadAllText(manifestPath);
            var manifest = PackageManifestParser.ParseRaw(yaml);
            if (manifest.Content is null || manifest.Content.Count == 0)
            {
                return [];
            }

            var result = new List<PackageContentEntry>(manifest.Content.Count);
            foreach (var entry in manifest.Content)
            {
                if (entry?.Definition is null)
                {
                    continue;
                }
                var kindKey = entry.Kind switch
                {
                    ArtefactKind.Unit => "unit",
                    ArtefactKind.Agent => "agent",
                    ArtefactKind.Skill => "skill",
                    ArtefactKind.Workflow => "workflow",
                    _ => entry.Kind.ToString().ToLowerInvariant(),
                };
                // The wire shape carries the reference string the manifest
                // declares (bare name for within-package, qualified for
                // cross-package, or the inline body's identifier).
                var name = entry.Definition.IsInline
                    ? (entry.Definition.InlineName ?? "<inline>")
                    : (entry.Definition.Reference ?? string.Empty);
                if (string.IsNullOrEmpty(name)) continue;
                result.Add(new PackageContentEntry(kindKey, name));
            }
            return result;
        }
        catch (Exception ex) when (ex is PackageParseException or YamlDotNet.Core.YamlException or IOException)
        {
            logger.LogWarning(ex,
                "Skipping content entries for package manifest '{Path}' because it could not be parsed.",
                manifestPath);
            return [];
        }
    }

    /// <summary>
    /// Reads the package manifest's <c>connectors:</c> block (#1670) so the
    /// install surfaces (wizard, CLI <c>spring package show</c>) can render
    /// one row per declared connector type. Failures fall back to an empty
    /// list — best-effort metadata, like <see cref="ReadPackageInputs"/>.
    /// </summary>
    private List<RequiredConnectorSummary> ReadConnectorDeclarations(string packageDir)
    {
        var manifestPath = FindManifestPath(packageDir);
        if (manifestPath is null)
        {
            return [];
        }

        try
        {
            var yaml = File.ReadAllText(manifestPath);
            var manifest = PackageManifestParser.ParseRaw(yaml);
            if (manifest.Connectors is null || manifest.Connectors.Count == 0)
            {
                return [];
            }

            var result = new List<RequiredConnectorSummary>(manifest.Connectors.Count);
            foreach (var c in manifest.Connectors)
            {
                if (string.IsNullOrWhiteSpace(c.Type))
                {
                    continue;
                }
                result.Add(new RequiredConnectorSummary(
                    Type: c.Type!,
                    Required: c.Required,
                    InheritAll: c.InheritAll,
                    InheritUnits: c.InheritUnits));
            }
            return result;
        }
        catch (Exception ex) when (ex is PackageParseException or YamlDotNet.Core.YamlException or IOException)
        {
            logger.LogWarning(ex,
                "Skipping connector declarations for package manifest '{Path}' because it could not be parsed.",
                manifestPath);
            return [];
        }
    }

    /// <summary>
    /// Read the <c>inputs:</c> block from the package's <c>package.yaml</c>
    /// (or <c>package.yml</c>) so the wizard / CLI can render input fields
    /// per declared input. A missing manifest, a malformed manifest, or a
    /// manifest without an inputs block all map to an empty list — browse
    /// is best-effort metadata and a malformed package should still appear
    /// in the catalog so the operator can investigate. Errors are logged
    /// at warning so misconfigurations don't disappear silently.
    /// </summary>
    private List<PackageInputSummary> ReadPackageInputs(string packageDir)
    {
        var manifestPath = FindManifestPath(packageDir);
        if (manifestPath is null)
        {
            return [];
        }

        try
        {
            var yaml = File.ReadAllText(manifestPath);
            var manifest = PackageManifestParser.ParseRaw(yaml);
            if (manifest.Inputs is null || manifest.Inputs.Count == 0)
            {
                return [];
            }

            var result = new List<PackageInputSummary>(manifest.Inputs.Count);
            foreach (var def in manifest.Inputs)
            {
                if (string.IsNullOrWhiteSpace(def.Name))
                {
                    continue;
                }

                result.Add(new PackageInputSummary(
                    Name: def.Name!,
                    Type: string.IsNullOrWhiteSpace(def.Type) ? "string" : def.Type!,
                    Required: def.Required,
                    Secret: def.Secret,
                    Description: def.Description,
                    Default: def.Default));
            }

            return result;
        }
        catch (Exception ex) when (ex is PackageParseException or YamlDotNet.Core.YamlException or IOException)
        {
            logger.LogWarning(
                ex,
                "Skipping inputs schema for package manifest '{Path}' because it could not be parsed.",
                manifestPath);
            return [];
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

        // Re-check resolved path is inside the packages root.
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

        var subDir = kind switch
        {
            ArtefactKind.Unit => "units",
            ArtefactKind.Agent => "agents",
            ArtefactKind.Skill => "skills",
            ArtefactKind.Workflow => "workflows",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        var extension = kind == ArtefactKind.Skill ? ".md" : ".yaml";
        var packageDir = Path.Combine(root, packageName);
        var candidate = Path.Combine(packageDir, subDir, artefactName + extension);

        // Re-check resolved path is inside the packages root.
        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(candidate);
        if (!fullCandidate.StartsWith(fullRoot, StringComparison.Ordinal))
        {
            return null;
        }

        if (!File.Exists(fullCandidate))
        {
            // Try .yml variant for unit/agent files.
            if (kind is ArtefactKind.Unit or ArtefactKind.Agent)
            {
                var ymlCandidate = Path.Combine(packageDir, subDir, artefactName + ".yml");
                var fullYml = Path.GetFullPath(ymlCandidate);
                if (fullYml.StartsWith(fullRoot, StringComparison.Ordinal) && File.Exists(fullYml))
                {
                    return await File.ReadAllTextAsync(fullYml, cancellationToken).ConfigureAwait(false);
                }
            }
            return null;
        }

        return await File.ReadAllTextAsync(fullCandidate, cancellationToken).ConfigureAwait(false);
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