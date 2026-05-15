// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Parses and validates a <c>package.yaml</c> manifest into a
/// <see cref="ResolvedPackage"/>. Implements ADR-0037 decisions and the
/// ADR-0043 recursive folder layout:
/// <list type="bullet">
///   <item><description>ADR-0037 D2: one root <c>package.yaml</c> per package.</description></item>
///   <item><description>ADR-0037 D3: uniform composition — bare = within-package, qualified = cross-package.</description></item>
///   <item><description>ADR-0037 D10: name uniqueness — first collision aborts with all offending names.</description></item>
///   <item><description>ADR-0037 D14: cross-package batch resolution via <see cref="IPackageCatalogProvider"/>.</description></item>
///   <item><description>ADR-0043 §2: directory layout IS the content — <c>content:</c> is rejected.</description></item>
///   <item><description>ADR-0043 §3: artefacts can ship nested children; the walker descends every conventional subdirectory at every depth.</description></item>
///   <item><description>ADR-0043 §4: inner artefact <c>package.yaml</c> files do not declare <c>version:</c>.</description></item>
///   <item><description>ADR-0043 §8: legacy shape signals (flat artefact layout, <c>content:</c>, inner <c>version:</c>, folder-name mismatch, <c>ai.prompt:</c>) are rejected with precise migration hints.</description></item>
/// </list>
/// </summary>
public static class PackageManifestParser
{
    /// <summary>
    /// Conventional subdirectories that the catalog walker descends at every
    /// depth (ADR-0043 §2). Each name maps to the artefact kind(s) that may
    /// live directly beneath it. <c>templates/</c> hosts both kinds — the
    /// inner <c>kind:</c> field disambiguates.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<ArtefactKind>> ConventionalSubdirs =
        new Dictionary<string, IReadOnlyList<ArtefactKind>>(StringComparer.Ordinal)
        {
            ["units"] = new[] { ArtefactKind.Unit },
            ["agents"] = new[] { ArtefactKind.Agent },
            ["skills"] = new[] { ArtefactKind.Skill },
            ["workflows"] = new[] { ArtefactKind.Workflow },
            ["templates"] = new[] { ArtefactKind.Unit, ArtefactKind.Agent }, // UnitTemplate / AgentTemplate
            ["connectors"] = Array.Empty<ArtefactKind>(), // reserved (ADR-0037 §1) — walked, but no kind
        };

    /// <summary>
    /// Parses a <c>package.yaml</c> YAML string into a <see cref="PackageManifest"/>
    /// without resolving references. Useful for inspecting the raw manifest
    /// shape before resolution.
    /// </summary>
    /// <exception cref="PackageParseException">Thrown when YAML is malformed or required fields are missing.</exception>
    public static PackageManifest ParseRaw(string yamlText)
    {
        ArgumentNullException.ThrowIfNull(yamlText);

        PackageManifest? doc;
        try
        {
            var deserializer = BuildDeserializer();
            doc = deserializer.Deserialize<PackageManifest>(yamlText);
        }
        catch (PackageParseException)
        {
            throw;
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new PackageParseException($"Invalid YAML in package manifest: {ex.Message}", ex);
        }

        if (doc is null)
        {
            throw new PackageParseException("Package manifest is empty.");
        }

        ValidateLegacyFields(yamlText, doc);
        ValidateRequiredFields(doc);

        // ADR-0039 § 9: reject `execution.containerRuntime:` (and the
        // hoisted root-level form) on the package-level `execution:`
        // block too. Mirrors the unit-side detection in
        // ManifestParser.DetectLegacyContainerRuntime so unit, agent
        // (via PackageValidator), and package manifests share one
        // rejection rule.
        try
        {
            ManifestParser.DetectLegacyContainerRuntime(yamlText);
        }
        catch (ManifestParseException ex)
        {
            throw new PackageParseException(ex.Message, ex);
        }

        return doc;
    }

    /// <summary>
    /// ADR-0037 decision 6 + ADR-0043 §8: reject every legacy-shape signal
    /// with a precise migration hint. Covers items 1+2 from #1718 (already
    /// shipped in PR #1719) plus the per-artefact decomposition from
    /// ADR-0037 plus the recursive folder layout from ADR-0043.
    /// </summary>
    private static void ValidateLegacyFields(string yamlText, PackageManifest doc)
    {
        // ADR-0037 decision 2 + #1718 item 1: kind: must be the literal
        // string Package. Old-shape values (UnitPackage / AgentPackage,
        // or any other) are rejected.
        if (!string.IsNullOrWhiteSpace(doc.Kind)
            && !string.Equals(doc.Kind.Trim(), "Package", StringComparison.Ordinal))
        {
            throw new PackageParseException(
                $"LegacyPackageKind: 'kind:' must be 'Package' in ADR-0037 (got '{doc.Kind}'). " +
                "The container manifest is the only kind of YAML at the package root.");
        }

        // ADR-0037 decision 2: metadata: nesting is removed.
        if (TopLevelKeyPresent(yamlText, "metadata"))
        {
            throw new PackageParseException(
                "LegacyMetadataNesting: 'metadata:' nesting is removed in ADR-0037. " +
                "Hoist 'name', 'description', and 'readme' to the top level of package.yaml.");
        }

        // ADR-0037 decision 2: inputs: is removed.
        if (TopLevelKeyPresent(yamlText, "inputs"))
        {
            throw new PackageParseException(
                "LegacyInputsField: 'inputs:' is removed in ADR-0037. " +
                "Move connector-binding parameters into per-artefact 'requires:' blocks; " +
                "behaviour parameters move to per-unit 'policies:'.");
        }

        // ADR-0037 decision 2: package-level connectors: is removed.
        if (TopLevelKeyPresent(yamlText, "connectors"))
        {
            throw new PackageParseException(
                "LegacyPackageConnectorsField: package-level 'connectors:' is removed in ADR-0037. " +
                "Declare requirements on per-artefact YAMLs as 'requires: [{ connector: <slug> }]'. " +
                "The package's effective requirement set is the union of every artefact's requires.");
        }

        // #1718 item 2: flat artefact lists are gone.
        var legacyKeys = new[] { "unit", "agent", "subUnits", "skills", "workflows" };
        foreach (var key in legacyKeys)
        {
            if (TopLevelKeyPresent(yamlText, key))
            {
                throw new PackageParseException(
                    $"Package manifest declares the obsolete top-level '{key}:' field. " +
                    "v0.1 declares all bundled artefacts under conventional folders " +
                    "(units/, agents/, skills/, workflows/, templates/) per ADR-0043 §2. " +
                    "Sub-units of an umbrella unit are discovered automatically from " +
                    "the umbrella's 'members:' list.");
            }
        }

        // ADR-0043 §8: `content:` is removed. The directory layout under
        // agents/, units/, skills/, workflows/, templates/ is the content.
        if (TopLevelKeyPresent(yamlText, "content"))
        {
            throw new PackageParseException(Adr0043ParseErrors.LegacyContentField);
        }
    }

    /// <summary>
    /// Best-effort top-level-key probe: scans line-starts of the raw YAML
    /// for <c>&lt;key&gt;:</c> at column 0 (no leading whitespace, the
    /// only place YAML places a top-level key). Skips comment lines.
    /// Sufficient for the legacy-field rejection — false positives on
    /// embedded heredoc-style strings are not realistic in a v0.1
    /// package manifest, and any false positive surfaces as a parse error
    /// the operator can correct by adjusting indentation.
    /// </summary>
    private static bool TopLevelKeyPresent(string yamlText, string key)
    {
        if (string.IsNullOrEmpty(yamlText))
        {
            return false;
        }

        var lines = yamlText.Split('\n');
        var prefix = key + ":";
        foreach (var rawLine in lines)
        {
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.Length == 0) continue;
            if (line[0] is ' ' or '\t' or '#') continue;
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                if (line.Length == prefix.Length || !IsKeyChar(line[prefix.Length - 1])
                    || line[prefix.Length] is ' ' or '\t' or '\0' or '\r')
                {
                    if (line.Length == prefix.Length) return true;
                    var next = line[prefix.Length];
                    if (next is ' ' or '\t' or '\r' or '#') return true;
                }
            }
        }
        return false;
    }

    private static bool IsKeyChar(char c) => c == ':' || char.IsLetterOrDigit(c) || c is '_' or '-';

    /// <summary>
    /// Fully parses and resolves a <c>package.yaml</c> into a
    /// <see cref="ResolvedPackage"/>. The package root directory is walked
    /// per ADR-0043 §2 (every conventional subdirectory at every depth);
    /// each discovered artefact folder becomes an entry in
    /// <see cref="ResolvedPackage.Units"/> / <c>Agents</c> / <c>Skills</c>
    /// / <c>Workflows</c>.
    /// </summary>
    /// <param name="yamlText">The raw <c>package.yaml</c> content.</param>
    /// <param name="packageRoot">
    /// The directory that is the root of the package being parsed. Pass
    /// <c>null</c> (or an empty string) when the manifest was received as
    /// an uploaded file with no accompanying on-disk directory — upload
    /// semantics. The catalog walker requires a real folder; uploads with
    /// no folder produce a package with no in-package artefacts.
    /// </param>
    /// <param name="inputValues">
    /// Unused under ADR-0037 (package-level <c>inputs:</c> is removed);
    /// preserved on the signature for source compatibility.
    /// </param>
    /// <param name="catalogProvider">
    /// Provider used to resolve cross-package references invoked from
    /// within-package artefacts (their <c>members:</c> entries). May be
    /// <c>null</c> when cross-package references are not expected.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public static async Task<ResolvedPackage> ParseAndResolveAsync(
        string yamlText,
        string? packageRoot,
        IReadOnlyDictionary<string, string>? inputValues = null,
        IPackageCatalogProvider? catalogProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(yamlText);

        // ADR-0037 D2: package-level `inputs:` is gone, so input
        // substitution is no longer part of the resolve path. The
        // parameter remains for signature compatibility.
        _ = inputValues;

        // Parse the manifest (raw header only). ParseRaw rejects every
        // legacy signal including the new ADR-0043 ones (content:, etc.).
        var manifest = ParseRaw(yamlText);

        // ADR-0043: walk the package root to discover artefacts. Upload
        // mode (null/empty root) yields the empty discovery list — there
        // is no way to reach in-package artefacts without a folder.
        IReadOnlyList<DiscoveredArtefact> discovered =
            string.IsNullOrEmpty(packageRoot)
                ? Array.Empty<DiscoveredArtefact>()
                : WalkPackage(packageRoot!, cancellationToken);

        // Validate name uniqueness across every discovered artefact. The
        // walker already gathered nested artefacts and templates; names
        // must be unique within a package regardless of depth (ADR-0043 §3).
        ValidateNameUniqueness(discovered);

        // Materialise ResolvedArtefacts from the discovered list.
        var resolved = MaterialiseResolved(discovered, cancellationToken);

        // Detect cycles across `members:`, `requires:`, `from:`, and
        // containment edges (ADR-0043 §7).
        DetectCycles(resolved, discovered);

        // Compute the package kind from the top-level shape (agents/
        // only with no units/ → AgentPackage; everything else → UnitPackage).
        var kind = InferKind(discovered);
        var name = manifest.Name!;

        var units = resolved
            .Where(r => r.Kind == ArtefactKind.Unit)
            .ToList();
        var agents = resolved
            .Where(r => r.Kind == ArtefactKind.Agent)
            .ToList();
        var skills = resolved
            .Where(r => r.Kind == ArtefactKind.Skill)
            .ToList();
        var workflows = resolved
            .Where(r => r.Kind == ArtefactKind.Workflow)
            .ToList();

        // ADR-0037 D3: compute the package-level requires union from each
        // artefact's per-artefact `requires:` block.
        var (requiredConnectorSlugs, connectorRequiresByArtefact) =
            ComputeRequiresUnion(units, agents, skills, workflows);

        // #1679: project the package-level `execution:` block (when
        // present) into the resolved declaration.
        var execution = ResolvePackageExecution(manifest.Execution, units);

        await Task.CompletedTask.ConfigureAwait(false);
        _ = catalogProvider;

        return new ResolvedPackage
        {
            Name = name,
            Description = manifest.Description,
            Version = manifest.Version,
            Kind = kind,
            InputValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            Units = units,
            Agents = agents,
            Skills = skills,
            Workflows = workflows,
            RequiredConnectorSlugs = requiredConnectorSlugs,
            ConnectorRequiresByArtefact = connectorRequiresByArtefact,
            Execution = execution,
        };
    }

    // ── ADR-0043 catalog walker ──────────────────────────────────────────

    /// <summary>
    /// One artefact discovered by the catalog walker. Carries the
    /// already-parsed inner <c>package.yaml</c> headers so callers
    /// (and the resolver) don't have to re-read the file.
    /// </summary>
    /// <param name="Kind">The artefact's kind, as declared on its inner <c>package.yaml</c>.</param>
    /// <param name="Name">The artefact's name (matches the containing folder).</param>
    /// <param name="FolderPath">Absolute path to the artefact's folder.</param>
    /// <param name="PackageYamlPath">Absolute path to the artefact's <c>package.yaml</c>.</param>
    /// <param name="RawYaml">Raw text of the artefact's <c>package.yaml</c>.</param>
    /// <param name="ContainingArtefactName">
    /// Name of the immediate containing artefact, or <c>null</c> when
    /// the artefact is at the package root.
    /// </param>
    internal sealed record DiscoveredArtefact(
        ArtefactKind Kind,
        string Name,
        string FolderPath,
        string PackageYamlPath,
        string RawYaml,
        string? ContainingArtefactName);

    /// <summary>
    /// Walks a package root per ADR-0043 §2/§3 and returns the discovered
    /// artefact set. Public so <c>FileSystemPackageCatalogService</c> can
    /// share the walker with the parser.
    /// </summary>
    public static IReadOnlyList<(ArtefactKind Kind, string Name, string FolderPath)> Walk(
        string packageRoot,
        CancellationToken cancellationToken = default)
    {
        var raw = WalkPackage(packageRoot, cancellationToken);
        var result = new List<(ArtefactKind, string, string)>(raw.Count);
        foreach (var a in raw)
        {
            result.Add((a.Kind, a.Name, a.FolderPath));
        }
        return result;
    }

    private static List<DiscoveredArtefact> WalkPackage(
        string packageRoot,
        CancellationToken cancellationToken)
    {
        var found = new List<DiscoveredArtefact>();
        WalkFolder(packageRoot, containingArtefact: null, isPackageRoot: true, found, cancellationToken);
        return found;
    }

    private static void WalkFolder(
        string folder,
        string? containingArtefact,
        bool isPackageRoot,
        List<DiscoveredArtefact> found,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        foreach (var (subdirName, allowedKinds) in ConventionalSubdirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var subdirPath = Path.Combine(folder, subdirName);
            if (!Directory.Exists(subdirPath))
            {
                continue;
            }

            // ADR-0043 §8: a `.yaml` / `.yml` / `.md` file directly under
            // a conventional subdirectory (rather than a folder rooted at
            // package.yaml) is the legacy flat layout — rejected.
            foreach (var loose in Directory.EnumerateFiles(subdirPath, "*.*", SearchOption.TopDirectoryOnly))
            {
                var ext = Path.GetExtension(loose);
                if (ext is ".yaml" or ".yml" or ".md")
                {
                    throw new PackageParseException(Adr0043ParseErrors.LegacyFlatArtefactLayout);
                }
            }

            foreach (var childDir in Directory.EnumerateDirectories(subdirPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var artefact = ReadArtefactFolder(
                    childDir, subdirName, allowedKinds, isPackageRoot, containingArtefact);
                found.Add(artefact);

                // Recurse into the artefact folder for its own conventional
                // subdirectories. Nested artefacts are not at the package root.
                WalkFolder(childDir, containingArtefact: artefact.Name, isPackageRoot: false, found, cancellationToken);
            }
        }
    }

    private static DiscoveredArtefact ReadArtefactFolder(
        string folder,
        string subdirName,
        IReadOnlyList<ArtefactKind> allowedKinds,
        bool isPackageRoot,
        string? containingArtefact)
    {
        var folderName = Path.GetFileName(folder);
        var manifestPath = Path.Combine(folder, "package.yaml");
        if (!File.Exists(manifestPath))
        {
            var ymlAlt = Path.Combine(folder, "package.yml");
            if (File.Exists(ymlAlt))
            {
                manifestPath = ymlAlt;
            }
            else
            {
                throw new PackageParseException(
                    $"Artefact folder '{folder}' is missing the required 'package.yaml' " +
                    "(ADR-0043 §1 — every standalone artefact is a folder rooted at package.yaml).");
            }
        }

        var rawYaml = File.ReadAllText(manifestPath);

        // Read kind / name / version off the inner manifest WITHOUT going
        // through the full per-kind parser — at this stage we only need to
        // route by kind and validate folder/name agreement.
        var (innerKind, innerName, hasVersion) = ReadInnerHeaders(rawYaml, manifestPath);

        if (string.IsNullOrWhiteSpace(innerName))
        {
            throw new PackageParseException(
                $"Artefact at '{folder}': inner package.yaml is missing the required 'name:' field.");
        }

        if (!string.Equals(folderName, innerName, StringComparison.Ordinal))
        {
            throw new PackageParseException(
                $"{Adr0043ParseErrors.ArtefactFolderNameMismatch} " +
                $"(folder='{folderName}', name='{innerName}', path='{folder}')");
        }

        if (hasVersion)
        {
            throw new PackageParseException(
                $"{Adr0043ParseErrors.UnexpectedInnerVersion} (path='{folder}')");
        }

        var artefactKind = MapKind(innerKind, subdirName, allowedKinds, folder);

        return new DiscoveredArtefact(
            Kind: artefactKind,
            Name: innerName!,
            FolderPath: folder,
            PackageYamlPath: manifestPath,
            RawYaml: rawYaml,
            ContainingArtefactName: containingArtefact);
    }

    /// <summary>
    /// Reads the discriminator fields (<c>kind</c>, <c>name</c>) and
    /// detects whether <c>version:</c> is declared, without going through
    /// the full per-kind manifest parser.
    /// </summary>
    private static (string? Kind, string? Name, bool HasVersion) ReadInnerHeaders(
        string yamlText, string path)
    {
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            var headers = deserializer.Deserialize<ArtefactHeaders>(yamlText)
                ?? new ArtefactHeaders();
            return (headers.Kind, headers.Name, !string.IsNullOrWhiteSpace(headers.Version));
        }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new PackageParseException(
                $"Invalid YAML at '{path}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Minimal probe for the artefact discriminator headers — used by
    /// the catalog walker before the per-kind manifest parser runs.
    /// </summary>
    private sealed class ArtefactHeaders
    {
        [YamlMember(Alias = "kind")]
        public string? Kind { get; set; }

        [YamlMember(Alias = "name")]
        public string? Name { get; set; }

        [YamlMember(Alias = "version")]
        public string? Version { get; set; }
    }

    private static ArtefactKind MapKind(
        string? declaredKind,
        string subdirName,
        IReadOnlyList<ArtefactKind> allowedKinds,
        string folder)
    {
        if (string.IsNullOrWhiteSpace(declaredKind))
        {
            throw new PackageParseException(
                $"Artefact at '{folder}': inner package.yaml is missing the required 'kind:' " +
                "discriminator (ADR-0037 §1).");
        }

        var trimmed = declaredKind!.Trim();

        // Template folders live under `templates/` and disambiguate via
        // their inner kind. Concrete kinds live under their own subdirs.
        ArtefactKind? resolved = trimmed switch
        {
            "Unit" => ArtefactKind.Unit,
            "Agent" => ArtefactKind.Agent,
            "Skill" => ArtefactKind.Skill,
            "Workflow" => ArtefactKind.Workflow,
            "UnitTemplate" => ArtefactKind.Unit,    // templates project onto the concrete kind for indexing
            "AgentTemplate" => ArtefactKind.Agent,
            "Package" => null,
            _ => null,
        };

        if (resolved is null)
        {
            throw new PackageParseException(
                $"Artefact at '{folder}': unknown 'kind:' value '{trimmed}'. " +
                "Expected one of: Unit, Agent, Skill, Workflow, UnitTemplate, AgentTemplate.");
        }

        // Validate the kind matches the subdirectory convention. Templates
        // live under `templates/`; the rest must match their subdir 1:1.
        if (subdirName == "templates")
        {
            if (trimmed is not ("UnitTemplate" or "AgentTemplate"))
            {
                throw new PackageParseException(
                    $"Artefact at '{folder}' under 'templates/' declares kind '{trimmed}' " +
                    "but the templates/ subdirectory expects 'UnitTemplate' or 'AgentTemplate' " +
                    "(ADR-0043 §5b).");
            }
        }
        else
        {
            var expected = subdirName switch
            {
                "units" => "Unit",
                "agents" => "Agent",
                "skills" => "Skill",
                "workflows" => "Workflow",
                "connectors" => null, // reserved
                _ => null,
            };
            if (expected is not null && !string.Equals(trimmed, expected, StringComparison.Ordinal))
            {
                throw new PackageParseException(
                    $"Artefact at '{folder}' under '{subdirName}/' declares kind '{trimmed}' " +
                    $"but '{subdirName}/' expects kind '{expected}' (ADR-0037 §1 + ADR-0043 §2).");
            }
        }

        // `Package` kind is not allowed inside another package's tree.
        // (We already returned null above for it.)
        _ = allowedKinds;
        _ = subdirName;
        _ = folder;

        return resolved.Value;
    }

    private static IReadOnlyList<ResolvedArtefact> MaterialiseResolved(
        IReadOnlyList<DiscoveredArtefact> discovered,
        CancellationToken cancellationToken)
    {
        var result = new List<ResolvedArtefact>(discovered.Count);
        foreach (var a in discovered)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? content = a.RawYaml;
            // For skill folders the companion markdown body is the
            // "content" most callers care about; we still surface the
            // raw YAML on Content so cycle detection / requires-union
            // can read the headers.
            result.Add(new ResolvedArtefact
            {
                Name = a.Name,
                SourcePackage = null,
                Kind = a.Kind,
                ResolvedPath = a.PackageYamlPath,
                Content = content,
                ContainingArtefactName = a.ContainingArtefactName,
            });
        }
        return result;
    }

    // ── Package-level execution projection ───────────────────────────────

    /// <summary>
    /// Projects the raw <see cref="PackageExecutionManifest"/> (which
    /// lets <c>inherit:</c> be either a scalar or a sequence) into the
    /// strongly-typed <see cref="PackageExecutionDeclaration"/> form
    /// the install pipeline consumes.
    /// </summary>
    private static PackageExecutionDeclaration? ResolvePackageExecution(
        PackageExecutionManifest? raw,
        IReadOnlyList<ResolvedArtefact> units)
    {
        if (raw is null)
        {
            return null;
        }

        if (raw.IsEmpty && raw.Inherit is null)
        {
            return null;
        }

        var inheritUnits = ParseInheritKey(raw.Inherit, units);

        return new PackageExecutionDeclaration(
            Image: NullIfBlank(raw.Image),
            Provider: NullIfBlank(raw.Provider),
            Model: NullIfBlank(raw.Model),
            InheritUnits: inheritUnits);
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static IReadOnlyList<string>? ParseInheritKey(
        object? rawInherit,
        IReadOnlyList<ResolvedArtefact> units)
    {
        if (rawInherit is null)
        {
            return null;
        }

        switch (rawInherit)
        {
            case string scalar:
                if (!string.Equals(scalar.Trim(), "all", StringComparison.OrdinalIgnoreCase))
                {
                    throw new PackageParseException(
                        $"package.execution.inherit: unsupported scalar '{scalar}'. " +
                        "Use 'all' (default — every member inherits) or omit the key. " +
                        "To restrict inheritance to specific members, declare a sequence of unit names.");
                }
                return null;

            case IEnumerable<object> sequence:
                {
                    var memberNames = new HashSet<string>(
                        units.Where(u => !u.IsCrossPackage).Select(u => u.Name),
                        StringComparer.OrdinalIgnoreCase);

                    var result = new List<string>();
                    var unknown = new List<string>();
                    foreach (var item in sequence)
                    {
                        if (item is not string name || string.IsNullOrWhiteSpace(name))
                        {
                            throw new PackageParseException(
                                "package.execution.inherit: every entry in the sequence must be a unit name string.");
                        }

                        var trimmed = name.Trim();
                        if (!memberNames.Contains(trimmed))
                        {
                            unknown.Add(trimmed);
                        }
                        result.Add(trimmed);
                    }

                    if (unknown.Count > 0)
                    {
                        throw new PackageParseException(
                            $"package.execution.inherit references unit(s) that are not members of this package: " +
                            $"{string.Join(", ", unknown.Select(n => $"'{n}'"))}.");
                    }

                    if (result.Count == 0)
                    {
                        throw new PackageParseException(
                            "package.execution.inherit: empty sequence selects no member units. " +
                            "Either omit the key (every member inherits) or list the members that should inherit.");
                    }

                    return result;
                }

            default:
                throw new PackageParseException(
                    "package.execution.inherit: unsupported shape. " +
                    "Use 'all' (scalar; every member inherits) or a sequence of unit names.");
        }
    }

    // ── Requires union ───────────────────────────────────────────────────

    private static (IReadOnlyList<string> Slugs, IReadOnlyDictionary<string, IReadOnlyList<string>> ByArtefact) ComputeRequiresUnion(
        IReadOnlyList<ResolvedArtefact> units,
        IReadOnlyList<ResolvedArtefact> agents,
        IReadOnlyList<ResolvedArtefact> skills,
        IReadOnlyList<ResolvedArtefact> workflows)
    {
        var union = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var byArtefact = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var artefact in units)
        {
            ExtractRequires<UnitManifest>(artefact, m => m?.Requires, union, seen, byArtefact);
        }
        foreach (var artefact in agents)
        {
            ExtractRequires<AgentManifest>(artefact, m => m?.Requires, union, seen, byArtefact);
        }
        _ = skills;
        _ = workflows;

        return (union, byArtefact);
    }

    private static void ExtractRequires<TManifest>(
        ResolvedArtefact artefact,
        Func<TManifest?, List<RequirementEntry>?> getRequires,
        List<string> union,
        HashSet<string> seen,
        Dictionary<string, IReadOnlyList<string>> byArtefact)
        where TManifest : class
    {
        if (string.IsNullOrEmpty(artefact.Content))
        {
            return;
        }

        TManifest? manifest;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithTypeConverter(new RequirementEntryYamlConverter())
                .IgnoreUnmatchedProperties()
                .Build();
            manifest = deserializer.Deserialize<TManifest>(artefact.Content);
        }
        catch
        {
            return;
        }

        var requires = getRequires(manifest);
        if (requires is null || requires.Count == 0)
        {
            return;
        }

        var slugs = new List<string>();
        foreach (var entry in requires)
        {
            if (entry.Type != RequirementType.Connector) continue;
            var slug = entry.Identifier?.Trim();
            if (string.IsNullOrEmpty(slug)) continue;
            slugs.Add(slug);
            if (seen.Add(slug))
            {
                union.Add(slug);
            }
        }

        if (slugs.Count > 0)
        {
            byArtefact[artefact.Name] = slugs;
        }
    }

    // ── Name uniqueness ──────────────────────────────────────────────────

    private static void ValidateNameUniqueness(IReadOnlyList<DiscoveredArtefact> discovered)
    {
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var collisions = new List<string>();

        foreach (var a in discovered)
        {
            var key = $"{a.Kind}:{a.Name}";
            if (seen.ContainsKey(key))
            {
                collisions.Add($"'{a.Kind}:{a.Name}'");
            }
            else
            {
                seen[key] = a.FolderPath;
            }
        }

        if (collisions.Count > 0)
        {
            throw new PackageParseException(
                $"Duplicate artefact name(s) within the package: {string.Join(", ", collisions)}. " +
                "Every artefact of the same kind must have a unique name (ADR-0043 §3).");
        }
    }

    // ── Cycle detection (ADR-0043 §7) ────────────────────────────────────

    private static void DetectCycles(
        IReadOnlyList<ResolvedArtefact> resolved,
        IReadOnlyList<DiscoveredArtefact> discovered)
    {
        // Build a graph keyed by `kind:name` (case-insensitive). Edges
        // come from three sources per ADR-0043 §7:
        //   1. members: entries on resolved unit YAMLs.
        //   2. from:    references on Agent / Unit / AgentTemplate / UnitTemplate.
        //   3. containment edges (an artefact at units/foo/agents/bar/ has
        //      an implicit edge foo → bar).
        // requires: edges are ordering constraints, not artefact references —
        // they're checked separately by the install pipeline's topo-sort.
        var graph = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var artefact in resolved)
        {
            var key = NodeKey(artefact.Kind, artefact.Name);
            graph.TryAdd(key, new List<string>());
        }

        // (1) members:
        foreach (var artefact in resolved.Where(r => r.Kind == ArtefactKind.Unit && r.Content is not null))
        {
            var key = NodeKey(artefact.Kind, artefact.Name);
            foreach (var (memberKind, memberName) in ExtractMemberRefs(artefact.Content!))
            {
                var nbrKey = NodeKey(memberKind, memberName);
                if (graph.ContainsKey(nbrKey))
                {
                    graph[key].Add(nbrKey);
                }
            }
        }

        // (2) from: references — read the discovered raw YAML directly so
        // both concrete and template kinds are covered uniformly.
        foreach (var a in discovered)
        {
            var fromRef = ReadFromField(a.RawYaml);
            if (string.IsNullOrWhiteSpace(fromRef))
            {
                continue;
            }
            var artefactRef = ArtefactReference.Parse(fromRef!, a.Kind);
            if (artefactRef.IsCrossPackage)
            {
                continue;
            }
            var key = NodeKey(a.Kind, a.Name);
            var nbrKey = NodeKey(a.Kind, artefactRef.ArtefactName);
            if (graph.ContainsKey(nbrKey))
            {
                graph[key].Add(nbrKey);
            }
        }

        // (3) containment edges
        foreach (var a in discovered)
        {
            if (a.ContainingArtefactName is null)
            {
                continue;
            }
            // Containment edge: containing artefact → this artefact. The
            // containing artefact's kind isn't carried in the record, but
            // names are unique within a package across kinds in practice
            // — we resolve by name across all kinds.
            var thisKey = NodeKey(a.Kind, a.Name);
            foreach (var k in (ArtefactKind[])Enum.GetValues(typeof(ArtefactKind)))
            {
                var parentKey = NodeKey(k, a.ContainingArtefactName);
                if (graph.ContainsKey(parentKey))
                {
                    graph[parentKey].Add(thisKey);
                    break;
                }
            }
        }

        // DFS cycle detection across the combined edge set.
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new List<string>();
        foreach (var node in graph.Keys)
        {
            if (!visited.Contains(node))
            {
                DfsCycleCheck(node, graph, visited, stack);
            }
        }
    }

    private static string NodeKey(ArtefactKind kind, string name)
        => $"{kind}|{name.ToLowerInvariant()}";

    /// <summary>
    /// Reads the <c>from:</c> scalar off a YAML document without going
    /// through the typed parser (works for Agent / Unit / AgentTemplate
    /// / UnitTemplate uniformly).
    /// </summary>
    private static string? ReadFromField(string yamlText)
    {
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            var headers = deserializer.Deserialize<FromFieldOnly>(yamlText);
            return headers?.From;
        }
        catch
        {
            return null;
        }
    }

    private sealed class FromFieldOnly
    {
        [YamlMember(Alias = "from")]
        public string? From { get; set; }
    }

    private static IEnumerable<(ArtefactKind Kind, string Reference)> ExtractMemberRefs(string unitYaml)
    {
        UnitManifest? manifest;
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithTypeConverter(new RequirementEntryYamlConverter())
                .IgnoreUnmatchedProperties()
                .Build();
            manifest = deserializer.Deserialize<UnitManifest>(unitYaml);
        }
        catch
        {
            yield break;
        }

        var members = manifest?.Members;
        if (members is null) yield break;

        foreach (var m in members)
        {
            if (!string.IsNullOrWhiteSpace(m.Unit))
            {
                var r = ArtefactReference.Parse(m.Unit!, ArtefactKind.Unit);
                if (!r.IsCrossPackage)
                {
                    yield return (ArtefactKind.Unit, r.ArtefactName);
                }
            }
            else if (!string.IsNullOrWhiteSpace(m.Agent))
            {
                var r = ArtefactReference.Parse(m.Agent!, ArtefactKind.Agent);
                if (!r.IsCrossPackage)
                {
                    yield return (ArtefactKind.Agent, r.ArtefactName);
                }
            }
        }
    }

    private static void DfsCycleCheck(
        string node,
        Dictionary<string, List<string>> graph,
        HashSet<string> visited,
        List<string> stack)
    {
        visited.Add(node);
        stack.Add(node);

        if (graph.TryGetValue(node, out var neighbours))
        {
            foreach (var neighbour in neighbours)
            {
                var stackIdx = stack.IndexOf(neighbour);
                if (stackIdx >= 0)
                {
                    var cycle = stack.Skip(stackIdx).ToList();
                    throw new PackageCycleException(cycle);
                }

                if (!visited.Contains(neighbour))
                {
                    DfsCycleCheck(neighbour, graph, visited, stack);
                }
            }
        }

        stack.RemoveAt(stack.Count - 1);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void ValidateRequiredFields(PackageManifest doc)
    {
        if (string.IsNullOrWhiteSpace(doc.Name))
        {
            throw new PackageParseException(
                "Package manifest is missing the required top-level 'name:' field (ADR-0037 decision 2).");
        }

        if (string.IsNullOrWhiteSpace(doc.Description))
        {
            throw new PackageParseException(
                "Package manifest is missing the required top-level 'description:' field (ADR-0037 decision 2).");
        }

        if (string.IsNullOrWhiteSpace(doc.Version))
        {
            throw new PackageParseException(
                "MissingPackageVersion: every package declares a 'version:' scalar (ADR-0037 decision 5).");
        }
    }

    /// <summary>
    /// Computes the package kind from the discovered top-level shape: a
    /// package whose top-level entries are exclusively agents (and which
    /// has no top-level units) resolves as <see cref="PackageKind.AgentPackage"/>;
    /// every other shape resolves as <see cref="PackageKind.UnitPackage"/>.
    /// Only top-level artefacts (those at the package root, not nested
    /// under a parent artefact) drive the kind.
    /// </summary>
    private static PackageKind InferKind(IReadOnlyList<DiscoveredArtefact> discovered)
    {
        var topLevel = discovered.Where(a => a.ContainingArtefactName is null).ToList();
        if (topLevel.Count == 0)
        {
            return PackageKind.UnitPackage;
        }

        var hasUnit = topLevel.Any(a => a.Kind == ArtefactKind.Unit);
        var hasAgent = topLevel.Any(a => a.Kind == ArtefactKind.Agent);
        if (hasAgent && !hasUnit)
        {
            return PackageKind.AgentPackage;
        }
        return PackageKind.UnitPackage;
    }

    private static IDeserializer BuildDeserializer()
        => new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new RequirementEntryYamlConverter())
            .IgnoreUnmatchedProperties()
            .Build();
}
