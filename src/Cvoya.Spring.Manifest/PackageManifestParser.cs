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
///   <item><description>ADR-0043 §3: artefacts can ship nested children; the walker descends every conventional subdirectory at every depth.</description></item>
///   <item><description>ADR-0043 §4: inner artefact <c>package.yaml</c> files do not declare <c>version:</c>.</description></item>
/// </list>
/// <para>
/// v0.1 has no back-compat guarantees (issue #2406). The parser is strict:
/// unknown top-level fields on the package manifest are a parse error.
/// </para>
/// </summary>
public static class PackageManifestParser
{
    /// <summary>
    /// Conventional subdirectories that the catalog walker descends at every
    /// depth (ADR-0043 §2, amended by ADR-0046 §2). Each name maps to the
    /// artefact kind(s) that may live directly beneath it. <c>templates/</c>
    /// hosts <see cref="ArtefactKind.Unit"/> / <see cref="ArtefactKind.Agent"/>
    /// / <see cref="ArtefactKind.HumanTemplate"/> — the inner <c>kind:</c>
    /// field disambiguates. ADR-0046 §2 removed <c>workflows/</c> and
    /// <c>connectors/</c>; both subdirectories surface a structured
    /// <see cref="PackageParseException"/> when encountered at any depth.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, IReadOnlyList<ArtefactKind>> ConventionalSubdirs =
        new Dictionary<string, IReadOnlyList<ArtefactKind>>(StringComparer.Ordinal)
        {
            ["units"] = new[] { ArtefactKind.Unit },
            ["agents"] = new[] { ArtefactKind.Agent },
            ["skills"] = new[] { ArtefactKind.Skill },
            ["templates"] = new[] { ArtefactKind.Unit, ArtefactKind.Agent, ArtefactKind.HumanTemplate },
        };

    /// <summary>
    /// Subdirectory names that were valid under ADR-0043 §2 but were removed
    /// from the package vocabulary in ADR-0046 §2. Encountering one at any
    /// depth raises a structured <see cref="PackageParseException"/> with
    /// the migration hint pointing at this ADR.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, string> RejectedSubdirs =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["workflows"] =
                "LegacyWorkflowsSubdir: `workflows/` is no longer part of the package vocabulary in v0.1 " +
                "(ADR-0046 §2). The one shipped workflow has been removed; re-introduce the conventional " +
                "directory if a future ADR adds a real workflow artefact type.",
            ["connectors"] =
                "LegacyConnectorsSubdir: `connectors/` is no longer part of the package vocabulary in v0.1 " +
                "(ADR-0046 §2). Connector bindings stay supported via `requires: [ { connector: <slug> } ]` " +
                "on consumer artefacts (ADR-0037 §3); the shipped artefact type has been removed.",
        };

    /// <summary>
    /// Parses a <c>package.yaml</c> YAML string into a <see cref="PackageManifest"/>
    /// without resolving references. Useful for inspecting the raw manifest
    /// shape before resolution. Strict parsing — unknown top-level fields
    /// are a parse error (issue #2406).
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

        ValidatePackageKind(doc);
        ValidateRequiredFields(doc);

        return doc;
    }

    private static void ValidatePackageKind(PackageManifest doc)
    {
        // ADR-0037 decision 2: kind: must be the literal string Package on
        // the package-root manifest.
        if (!string.IsNullOrWhiteSpace(doc.Kind)
            && !string.Equals(doc.Kind.Trim(), "Package", StringComparison.Ordinal))
        {
            throw new PackageParseException(
                $"Package manifest 'kind:' must be 'Package' (got '{doc.Kind}'). " +
                "The container manifest is the only kind of YAML at the package root.");
        }
    }

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

        // ADR-0043 §5g: a unit's `members:` list may carry inline bodies
        // (mappings under `agent:` / `unit:`) in addition to bare scalar
        // references. Each inline body becomes a fresh concrete artefact
        // peer in the package's resolved set so the rest of the install
        // pipeline (template stamping, member resolution, activation)
        // walks it identically to a disk-discovered artefact.
        resolved = ExpandInlineMembers(resolved);

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
        var humanTemplates = resolved
            .Where(r => r.Kind == ArtefactKind.HumanTemplate)
            .ToList();

        // ADR-0037 D3: compute the package-level requires union from each
        // artefact's per-artefact `requires:` block.
        var (requiredConnectorSlugs, connectorRequiresByArtefact) =
            ComputeRequiresUnion(units, agents, skills, humanTemplates);

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
            HumanTemplates = humanTemplates,
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

        // ADR-0046 §2: `workflows/` and `connectors/` are dropped from the
        // vocabulary. Reject either subdirectory at any depth with the
        // structured error from RejectedSubdirs so authors see the migration
        // hint, not a silent skip.
        foreach (var (rejectedName, message) in RejectedSubdirs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rejectedPath = Path.Combine(folder, rejectedName);
            if (Directory.Exists(rejectedPath))
            {
                throw new PackageParseException(
                    $"{message} (path='{rejectedPath}')");
            }
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

        // ADR-0046 §2: `kind: Workflow` is rejected at parse time. The
        // shipped workflow artefact type is gone; the explicit error
        // surfaces the migration hint instead of falling through to the
        // generic "unknown kind" branch.
        if (string.Equals(trimmed, "Workflow", StringComparison.Ordinal))
        {
            throw new PackageParseException(
                $"LegacyWorkflowKind: artefact at '{folder}' declares 'kind: Workflow' but " +
                "the Workflow artefact type was dropped in v0.1 (ADR-0046 §2). Connector " +
                "bindings continue to work via `requires:` on consumer artefacts.");
        }

        // Template folders live under `templates/` and disambiguate via
        // their inner kind. Concrete kinds live under their own subdirs.
        ArtefactKind? resolved = trimmed switch
        {
            "Unit" => ArtefactKind.Unit,
            "Agent" => ArtefactKind.Agent,
            "Skill" => ArtefactKind.Skill,
            "UnitTemplate" => ArtefactKind.Unit,    // templates project onto the concrete kind for indexing
            "AgentTemplate" => ArtefactKind.Agent,
            "HumanTemplate" => ArtefactKind.HumanTemplate,
            "Package" => null,
            _ => null,
        };

        if (resolved is null)
        {
            throw new PackageParseException(
                $"Artefact at '{folder}': unknown 'kind:' value '{trimmed}'. " +
                "Expected one of: Unit, Agent, Skill, UnitTemplate, AgentTemplate, HumanTemplate.");
        }

        // Validate the kind matches the subdirectory convention. Templates
        // live under `templates/`; the rest must match their subdir 1:1.
        if (subdirName == "templates")
        {
            if (trimmed is not ("UnitTemplate" or "AgentTemplate" or "HumanTemplate"))
            {
                throw new PackageParseException(
                    $"Artefact at '{folder}' under 'templates/' declares kind '{trimmed}' " +
                    "but the templates/ subdirectory expects 'UnitTemplate', 'AgentTemplate', " +
                    "or 'HumanTemplate' (ADR-0043 §5b + ADR-0046 §4).");
            }
        }
        else
        {
            var expected = subdirName switch
            {
                "units" => "Unit",
                "agents" => "Agent",
                "skills" => "Skill",
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

    /// <summary>
    /// ADR-0043 §5g: walks every unit artefact's <c>members:</c> list and
    /// synthesises a fresh <see cref="ResolvedArtefact"/> per inline-body
    /// entry (an <c>agent:</c> / <c>unit:</c> mapping rather than a bare
    /// scalar). Each synthesised artefact is wrapped under the kind's root
    /// document shape (<c>apiVersion</c>, <c>kind</c>, plus the body fields)
    /// so the rest of the install pipeline parses it identically to a
    /// disk-discovered artefact. The synthesised artefact's
    /// <see cref="ResolvedArtefact.ContainingArtefactName"/> records its
    /// owning unit, mirroring the §3 nested-artefact containment edge.
    /// </summary>
    /// <remarks>
    /// Inline bodies that declare <c>from:</c> ride through unchanged — the
    /// <see cref="TemplateResolver"/> picks them up on the same pass it uses
    /// for disk-discovered concrete artefacts and merges the template body
    /// in per §5d. The local-symbol the owning unit references is the
    /// inline body's <c>name:</c> field (the bare-scalar form's address).
    /// </remarks>
    private static IReadOnlyList<ResolvedArtefact> ExpandInlineMembers(
        IReadOnlyList<ResolvedArtefact> resolved)
    {
        // Index discovered names by kind so synthesis can reject duplicates
        // up-front rather than rely on the downstream uniqueness check
        // ignoring the synthesised peers.
        var existingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var artefact in resolved)
        {
            existingNames.Add($"{artefact.Kind}|{artefact.Name}");
        }

        var synthesised = new List<ResolvedArtefact>();
        foreach (var artefact in resolved)
        {
            if (artefact.Kind != ArtefactKind.Unit
                || string.IsNullOrEmpty(artefact.Content))
            {
                continue;
            }

            UnitManifest? unit;
            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .WithTypeConverter(new RequirementEntryYamlConverter())
                    .WithTypeConverter(new InlineArtefactDefinitionYamlConverter())
                    .IgnoreUnmatchedProperties()
                    .Build();
                unit = deserializer.Deserialize<UnitManifest>(artefact.Content);
            }
            catch
            {
                // The full per-kind parser will surface the same failure
                // later in the pipeline; skip inline expansion for this
                // unit so the cycle / uniqueness checks still see what
                // they always saw.
                continue;
            }

            if (unit?.Members is null || unit.Members.Count == 0)
            {
                continue;
            }

            foreach (var member in unit.Members)
            {
                // ADR-0046 §1: members may also carry a `human:` slot. The
                // `human:` slot is inline-only (humans own no sub-artefacts,
                // ADR-0046 §6), and the install activator materialises each
                // declaration into a fresh `HumanEntity` row at install time
                // rather than synthesising a peer artefact in the resolved
                // set. We therefore skip the human slot here — the cycle
                // detector and name-uniqueness check have nothing to do with
                // package-declared humans (which have no name-symbol shared
                // with the catalog).
                var inline = member.Agent?.IsInline == true ? member.Agent
                           : member.Unit?.IsInline == true ? member.Unit
                           : null;
                if (inline?.InlineBody is null)
                {
                    continue;
                }

                var kind = member.Agent?.IsInline == true
                    ? ArtefactKind.Agent
                    : ArtefactKind.Unit;
                var name = inline.InlineName!;
                if (string.IsNullOrWhiteSpace(name)
                    || string.Equals(name, "<inline>", StringComparison.Ordinal))
                {
                    throw new PackageParseException(
                        $"unit '{artefact.Name}': inline {kind.ToString().ToLowerInvariant()} member " +
                        "(ADR-0043 §5g) is missing the required 'name:' field. The body's name is " +
                        "the local symbol the rest of the unit references.");
                }

                var key = $"{kind}|{name}";
                if (!existingNames.Add(key))
                {
                    // Either two inline members shadow each other or an
                    // inline member shadows a disk-discovered artefact.
                    // Treat both as the same name-uniqueness violation the
                    // ADR-0043 §3 downstream check enforces.
                    throw new PackageParseException(
                        $"Duplicate artefact name '{kind}:{name}' introduced by an inline member of " +
                        $"unit '{artefact.Name}' (ADR-0043 §3 + §5g). Inline members must use names " +
                        "distinct from every other artefact in the package.");
                }

                synthesised.Add(new ResolvedArtefact
                {
                    Name = name,
                    SourcePackage = null,
                    Kind = kind,
                    // Inline members have no file of their own; inherit the
                    // containing unit's path so path-aware consumers (the
                    // validator's annotation surface, the install activator's
                    // diagnostics) point at the YAML that actually declares
                    // them. The activator's symbol-map walks address the
                    // synthesised artefact by name anyway, so reusing the
                    // unit's path here is purely a diagnostic hint.
                    ResolvedPath = artefact.ResolvedPath,
                    Content = WrapInlineBody(kind, inline.InlineBody),
                    ContainingArtefactName = artefact.Name,
                });
            }
        }

        if (synthesised.Count == 0)
        {
            return resolved;
        }

        var combined = new List<ResolvedArtefact>(resolved.Count + synthesised.Count);
        combined.AddRange(resolved);
        combined.AddRange(synthesised);
        return combined;
    }

    /// <summary>
    /// Wraps an inline body (re-serialised mapping captured by
    /// <see cref="InlineArtefactDefinitionYamlConverter"/>) with the
    /// <c>apiVersion:</c> + <c>kind:</c> header expected by the per-kind
    /// manifest parser so the downstream pipeline reads the synthesised
    /// artefact identically to a disk-discovered one. The body's own
    /// <c>kind:</c> (when present) is overwritten — inline members are
    /// always concrete (<c>Agent</c> / <c>Unit</c>), not templates.
    /// </summary>
    private static string WrapInlineBody(ArtefactKind kind, string inlineBody)
    {
        var kindHeader = kind switch
        {
            ArtefactKind.Agent => "Agent",
            ArtefactKind.Unit => "Unit",
            _ => throw new InvalidOperationException(
                $"Inline members are only supported for Agent / Unit, not {kind}."),
        };

        // Parse the inline body, then rewrite the kind-discriminator scalars
        // to the wrapper's values so the per-kind manifest parser accepts the
        // synthesised content. The body usually omits both apiVersion: and
        // kind: (member bodies are sugar over the disk-discovered shape).
        var stream = new YamlDotNet.RepresentationModel.YamlStream();
        using (var reader = new StringReader(inlineBody))
        {
            stream.Load(reader);
        }

        var root = stream.Documents.Count > 0
            ? stream.Documents[0].RootNode as YamlDotNet.RepresentationModel.YamlMappingNode
            : null;
        root ??= new YamlDotNet.RepresentationModel.YamlMappingNode();

        SetScalarChild(root, "apiVersion", "spring.voyage/v1");
        SetScalarChild(root, "kind", kindHeader);

        using var writer = new StringWriter();
        var output = new YamlDotNet.RepresentationModel.YamlStream(
            new YamlDotNet.RepresentationModel.YamlDocument(root));
        output.Save(writer, assignAnchors: false);
        var text = writer.ToString();
        var trimmedEnd = text.TrimEnd();
        if (trimmedEnd.EndsWith("...", StringComparison.Ordinal))
        {
            trimmedEnd = trimmedEnd[..^3].TrimEnd();
        }
        return trimmedEnd + "\n";
    }

    private static void SetScalarChild(
        YamlDotNet.RepresentationModel.YamlMappingNode parent,
        string key,
        string value)
    {
        var keyNode = new YamlDotNet.RepresentationModel.YamlScalarNode(key);
        parent.Children[keyNode] = new YamlDotNet.RepresentationModel.YamlScalarNode(value);
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
        IReadOnlyList<ResolvedArtefact> humanTemplates)
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
        _ = humanTemplates;

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
                .WithTypeConverter(new InlineArtefactDefinitionYamlConverter())
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
                .WithTypeConverter(new InlineArtefactDefinitionYamlConverter())
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
            // Unit slot — bare reference or inline body (ADR-0043 §5g). The
            // inline body's `name:` is the local symbol the cycle detector
            // edge points at; the synthesised artefact lands in the package's
            // resolved set just like a disk-discovered one.
            var unitSymbol = m.UnitName;
            if (!string.IsNullOrWhiteSpace(unitSymbol))
            {
                var r = ArtefactReference.Parse(unitSymbol!, ArtefactKind.Unit);
                if (!r.IsCrossPackage)
                {
                    yield return (ArtefactKind.Unit, r.ArtefactName);
                }
                continue;
            }

            var agentSymbol = m.AgentName;
            if (!string.IsNullOrWhiteSpace(agentSymbol))
            {
                var r = ArtefactReference.Parse(agentSymbol!, ArtefactKind.Agent);
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
            .Build();
}
