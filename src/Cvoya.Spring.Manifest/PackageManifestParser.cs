// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Parses and validates a <c>package.yaml</c> manifest into a
/// <see cref="ResolvedPackage"/>. Implements ADR-0035 decisions 2, 3, 8,
/// 10, and 14:
/// <list type="bullet">
///   <item><description>Decision 2: One root <c>package.yaml</c> per package.</description></item>
///   <item><description>Decision 3: Uniform composition — bare = within-package, qualified = cross-package.</description></item>
///   <item><description>Decision 8: Scalar <c>${{ inputs.foo }}</c> substitution before reference resolution.</description></item>
///   <item><description>Decision 10: Name uniqueness — first collision aborts with all offending names.</description></item>
///   <item><description>Decision 14: Cross-package batch resolution via <see cref="IPackageCatalogProvider"/>.</description></item>
/// </list>
/// </summary>
public static class PackageManifestParser
{
    private static readonly Regex InputInterpolationPattern =
        new(@"\$\{\{\s*inputs\.([A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);

    /// <summary>
    /// Parses a <c>package.yaml</c> YAML string into a <see cref="PackageManifest"/>
    /// without resolving references or substituting inputs. Useful for inspecting
    /// the raw manifest shape before resolution.
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
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new PackageParseException($"Invalid YAML in package manifest: {ex.Message}", ex);
        }

        if (doc is null)
        {
            throw new PackageParseException("Package manifest is empty.");
        }

        ValidateRequiredFields(doc);
        ValidatePackageGrammar(doc);
        return doc;
    }

    /// <summary>
    /// Validates the package-level grammar against the v0.1 rules introduced
    /// by #1629 PR7 — namely that every reference field rejects path-style
    /// values (<c>scheme://...</c>). Runs from <see cref="ParseRaw"/> so the
    /// rejection fires even on paths that never make it as far as
    /// <see cref="ParseAndResolveAsync"/> (e.g. export tooling that only
    /// inspects the schema).
    /// </summary>
    private static void ValidatePackageGrammar(PackageManifest doc)
    {
        if (doc.Unit is { IsInline: false } unitSlot)
        {
            LocalSymbolValidator.RejectPathStyleReference(
                unitSlot.Reference, "unit", GrammarLayer.PackageManifest);
        }
        if (doc.Agent is { IsInline: false } agentSlot)
        {
            LocalSymbolValidator.RejectPathStyleReference(
                agentSlot.Reference, "agent", GrammarLayer.PackageManifest);
        }
        if (doc.SubUnits is { Count: > 0 })
        {
            for (var i = 0; i < doc.SubUnits.Count; i++)
            {
                LocalSymbolValidator.RejectPathStyleReference(
                    doc.SubUnits[i], $"subUnits[{i}]", GrammarLayer.PackageManifest);
            }
        }
        if (doc.Skills is { Count: > 0 })
        {
            for (var i = 0; i < doc.Skills.Count; i++)
            {
                LocalSymbolValidator.RejectPathStyleReference(
                    doc.Skills[i], $"skills[{i}]", GrammarLayer.PackageManifest);
            }
        }
        if (doc.Workflows is { Count: > 0 })
        {
            for (var i = 0; i < doc.Workflows.Count; i++)
            {
                LocalSymbolValidator.RejectPathStyleReference(
                    doc.Workflows[i], $"workflows[{i}]", GrammarLayer.PackageManifest);
            }
        }
    }

    /// <summary>
    /// Fully parses and resolves a <c>package.yaml</c> into a
    /// <see cref="ResolvedPackage"/>. Steps (per ADR-0035 decision 8):
    /// <list type="number">
    ///   <item><description>Validate input schema against supplied values.</description></item>
    ///   <item><description>Perform scalar <c>${{ inputs.* }}</c> substitution on the YAML text.</description></item>
    ///   <item><description>Parse the substituted YAML.</description></item>
    ///   <item><description>Resolve all artefact references (within-package + cross-package).</description></item>
    ///   <item><description>Detect cycles in the reference graph.</description></item>
    ///   <item><description>Validate name uniqueness within the package.</description></item>
    /// </list>
    /// Cross-package artefacts must be self-contained — input expressions in
    /// cross-package bodies raise <see cref="CrossPackageArtefactNotSelfContainedException"/>.
    /// Each install is independent; the consuming package does not share its
    /// input scope with referenced packages.
    /// </summary>
    /// <param name="yamlText">The raw <c>package.yaml</c> content.</param>
    /// <param name="packageRoot">
    /// The directory that is the root of the package being parsed.
    /// Used to resolve within-package bare references. Pass <c>null</c> (or
    /// an empty string) when the manifest was received as an uploaded file
    /// with no accompanying on-disk directory — upload semantics. In that
    /// mode, any bare (local) artefact reference raises
    /// <see cref="PackageUploadHasLocalRefException"/>; cross-package
    /// references still resolve via <paramref name="catalogProvider"/>.
    /// </param>
    /// <param name="inputValues">
    /// Caller-supplied input values, keyed by input name. Secret inputs
    /// should be supplied as their secret reference value (e.g.
    /// <c>secret://my-tenant/api-key</c>).
    /// </param>
    /// <param name="catalogProvider">
    /// Provider used to resolve cross-package references. May be
    /// <c>null</c> when cross-package references are not expected.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The fully resolved package.</returns>
    /// <exception cref="PackageUploadHasLocalRefException">
    /// Thrown when <paramref name="packageRoot"/> is <c>null</c> or empty and
    /// the manifest contains one or more bare (within-package) artefact references.
    /// </exception>
    public static async Task<ResolvedPackage> ParseAndResolveAsync(
        string yamlText,
        string? packageRoot,
        IReadOnlyDictionary<string, string>? inputValues = null,
        IPackageCatalogProvider? catalogProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(yamlText);

        inputValues ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Step 1: Parse raw to discover the inputs schema.
        var rawManifest = ParseRaw(yamlText);

        // Step 2: Validate inputs (required, type, secret).
        ValidateInputs(rawManifest.Inputs, inputValues);

        // Step 3: Substitute ${{ inputs.* }} in the YAML text.
        var substituted = SubstituteInputs(yamlText, rawManifest.Inputs ?? [], inputValues);

        // Step 4: Re-parse the substituted YAML.
        PackageManifest manifest;
        try
        {
            manifest = ParseRaw(substituted);
        }
        catch (PackageParseException ex)
        {
            throw new PackageParseException(
                $"Package manifest failed to parse after input substitution: {ex.Message}", ex);
        }

        // Step 5: Build ArtefactReference lists from the manifest.
        var allRefs = CollectReferences(manifest);

        // Step 6: Validate name uniqueness.
        ValidateNameUniqueness(allRefs.Select(e => e.Reference).ToList());

        // Step 7: Resolve all references (passing input schema + values so
        // within-package local artefact bodies get the same substitution pass).
        var resolved = await ResolveReferencesAsync(
            allRefs, packageRoot, manifest.Inputs ?? [], inputValues,
            catalogProvider, cancellationToken).ConfigureAwait(false);

        // Step 8: Detect cycles.
        DetectCycles(resolved);

        var kind = ParseKind(manifest.Kind!);
        var name = manifest.Metadata!.Name!;

        // Build resolved artefact lists per kind.
        var units = resolved
            .Where(r => r.Reference.Kind == ArtefactKind.Unit)
            .Select(r => r.Artefact)
            .ToList();
        var agents = resolved
            .Where(r => r.Reference.Kind == ArtefactKind.Agent)
            .Select(r => r.Artefact)
            .ToList();
        var skills = resolved
            .Where(r => r.Reference.Kind == ArtefactKind.Skill)
            .Select(r => r.Artefact)
            .ToList();
        var workflows = resolved
            .Where(r => r.Reference.Kind == ArtefactKind.Workflow)
            .Select(r => r.Artefact)
            .ToList();

        // Build the resolved input values map (with defaults applied).
        var finalInputValues = BuildFinalInputValues(manifest.Inputs ?? [], inputValues);

        return new ResolvedPackage
        {
            Name = name,
            Description = manifest.Metadata.Description,
            Kind = kind,
            InputValues = finalInputValues,
            Units = units,
            Agents = agents,
            Skills = skills,
            Workflows = workflows,
        };
    }

    // ---- Input validation & substitution --------------------------------

    /// <summary>
    /// Validates the supplied input values against the package's input
    /// schema. Throws <see cref="PackageInputValidationException"/> for
    /// the first failing input.
    /// </summary>
    public static void ValidateInputs(
        List<PackageInputDefinition>? schema,
        IReadOnlyDictionary<string, string> supplied)
    {
        if (schema is null || schema.Count == 0)
        {
            return;
        }

        foreach (var def in schema)
        {
            var name = def.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var hasValue = supplied.TryGetValue(name, out var value);

            // Required check.
            if (def.Required && !hasValue && def.Default is null)
            {
                throw new PackageInputValidationException(
                    name,
                    $"Input '{name}' is required but was not supplied.");
            }

            if (!hasValue)
            {
                value = def.Default;
            }

            if (value is null)
            {
                continue;
            }

            // Type check (skipped for secret — the caller supplies a secret reference).
            if (!def.Secret)
            {
                ValidateInputType(def, value);
            }
        }
    }

    private static void ValidateInputType(PackageInputDefinition def, string value)
    {
        var type = (def.Type ?? "string").Trim().ToLowerInvariant();
        switch (type)
        {
            case "string":
                // Any string is valid.
                break;
            case "int":
            case "integer":
                if (!int.TryParse(value, out _))
                {
                    throw new PackageInputValidationException(
                        def.Name!,
                        $"Input '{def.Name}' expects type 'int' but received '{value}'.");
                }
                break;
            case "bool":
            case "boolean":
                if (!bool.TryParse(value, out _) &&
                    value is not ("true" or "false" or "1" or "0"))
                {
                    throw new PackageInputValidationException(
                        def.Name!,
                        $"Input '{def.Name}' expects type 'bool' but received '{value}'.");
                }
                break;
            default:
                // Unknown types treated as string for forward compatibility.
                break;
        }
    }

    /// <summary>
    /// Performs scalar <c>${{ inputs.foo }}</c> substitution on the raw
    /// YAML text. Substitution errors (undeclared input name) become
    /// <see cref="PackageInputValidationException"/>.
    /// </summary>
    public static string SubstituteInputs(
        string yamlText,
        IReadOnlyList<PackageInputDefinition> schema,
        IReadOnlyDictionary<string, string> supplied)
    {
        // Build effective values map (supplied values + defaults).
        var effective = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in schema)
        {
            if (string.IsNullOrWhiteSpace(def.Name))
            {
                continue;
            }

            if (supplied.TryGetValue(def.Name, out var v))
            {
                // Secret inputs: store reference form.
                effective[def.Name] = def.Secret
                    ? (v.StartsWith("secret://", StringComparison.Ordinal) ? v : $"secret://{v}")
                    : v;
            }
            else if (def.Default is not null)
            {
                effective[def.Name] = def.Default;
            }
        }

        return InputInterpolationPattern.Replace(yamlText, match =>
        {
            var inputName = match.Groups[1].Value;
            if (effective.TryGetValue(inputName, out var replacement))
            {
                return replacement;
            }

            // Reference to an input name not in the schema.
            throw new PackageInputValidationException(
                inputName,
                $"Input expression '${{{{ inputs.{inputName} }}}}' references an undeclared input '{inputName}'.");
        });
    }

    // ---- Reference collection -------------------------------------------

    /// <summary>
    /// One entry produced by <see cref="CollectReferences"/>. Carries the
    /// parsed <see cref="ArtefactReference"/> alongside an optional inline
    /// body when the operator declared the artefact directly in
    /// <c>package.yaml</c> instead of as a bare/qualified ref.
    /// </summary>
    private sealed record ArtefactCollectEntry(ArtefactReference Reference, string? InlineBody);

    private static List<ArtefactCollectEntry> CollectReferences(PackageManifest manifest)
    {
        // Path-style rejection lives in ValidatePackageGrammar (called from
        // ParseRaw) so it fires even on schema-only inspection paths. By the
        // time we reach CollectReferences, every reference field has already
        // been screened for the obsolete `scheme://...` form.

        var refs = new List<ArtefactCollectEntry>();

        AddSlot(refs, manifest.Unit, ArtefactKind.Unit);
        AddSlot(refs, manifest.Agent, ArtefactKind.Agent);

        if (manifest.SubUnits is { Count: > 0 })
        {
            foreach (var s in manifest.SubUnits.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                refs.Add(new ArtefactCollectEntry(
                    ArtefactReference.Parse(s, ArtefactKind.Unit), InlineBody: null));
            }
        }

        if (manifest.Skills is { Count: > 0 })
        {
            foreach (var s in manifest.Skills.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                refs.Add(new ArtefactCollectEntry(
                    ArtefactReference.Parse(s, ArtefactKind.Skill), InlineBody: null));
            }
        }

        if (manifest.Workflows is { Count: > 0 })
        {
            foreach (var w in manifest.Workflows.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                refs.Add(new ArtefactCollectEntry(
                    ArtefactReference.Parse(w, ArtefactKind.Workflow), InlineBody: null));
            }
        }

        return refs;
    }

    private static void AddSlot(
        List<ArtefactCollectEntry> refs,
        InlineArtefactDefinition? slot,
        ArtefactKind kind)
    {
        if (slot is null)
        {
            return;
        }

        if (slot.IsInline)
        {
            // Inline body: synthesise a within-package ArtefactReference so
            // name-uniqueness + cycle checks still operate on a stable name.
            // The body is captured verbatim and re-wrapped under the kind
            // root key so the install activator can consume it through the
            // same `ManifestParser.Parse` path as a disk-resolved reference.
            var inlineName = slot.InlineName ?? "<inline>";
            var rootKey = kind == ArtefactKind.Agent ? "agent" : "unit";
            var wrapped = WrapInlineBody(rootKey, slot.InlineBody!);
            refs.Add(new ArtefactCollectEntry(
                new ArtefactReference(inlineName, PackageName: null, inlineName, kind),
                InlineBody: wrapped));
            return;
        }

        if (!string.IsNullOrWhiteSpace(slot.Reference))
        {
            refs.Add(new ArtefactCollectEntry(
                ArtefactReference.Parse(slot.Reference, kind), InlineBody: null));
        }
    }

    private static string WrapInlineBody(string rootKey, string body)
    {
        // The captured body is already YAML emitted by YamlDotNet (block
        // mapping, indented at column 0). Re-indent each line by two spaces
        // and prepend the kind root key so the result is a fully-formed
        // YAML document that ManifestParser.Parse / agent activation can
        // consume.
        var lines = body.Split('\n');
        var indented = new System.Text.StringBuilder();
        indented.Append(rootKey).Append(":\n");
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                indented.Append('\n');
                continue;
            }
            indented.Append("  ").Append(line).Append('\n');
        }
        return indented.ToString();
    }

    // ---- Name uniqueness ------------------------------------------------

    private static void ValidateNameUniqueness(List<ArtefactReference> refs)
    {
        var seen = new Dictionary<string, ArtefactKind>(StringComparer.OrdinalIgnoreCase);
        var collisions = new List<string>();

        foreach (var r in refs)
        {
            var key = $"{r.Kind}:{r.ArtefactName}";
            if (seen.ContainsKey(key))
            {
                collisions.Add($"'{r.Kind}:{r.ArtefactName}'");
            }
            else
            {
                seen[key] = r.Kind;
            }
        }

        if (collisions.Count > 0)
        {
            throw new PackageParseException(
                $"Duplicate artefact name(s) within the package: {string.Join(", ", collisions)}. " +
                "Every artefact of the same type must have a unique name.");
        }
    }

    // ---- Reference resolution ------------------------------------------

    private record RefResolution(ArtefactReference Reference, ResolvedArtefact Artefact);

    private static async Task<List<RefResolution>> ResolveReferencesAsync(
        List<ArtefactCollectEntry> refs,
        string? packageRoot,
        IReadOnlyList<PackageInputDefinition> inputSchema,
        IReadOnlyDictionary<string, string> inputValues,
        IPackageCatalogProvider? catalogProvider,
        CancellationToken cancellationToken)
    {
        var result = new List<RefResolution>();

        // When packageRoot is null/empty we are in upload mode. Collect ALL
        // local refs before throwing so the operator sees the full list at once.
        // Inline definitions are by construction self-contained — they live
        // entirely in the uploaded package.yaml — so they do NOT trigger the
        // upload-mode local-ref rejection.
        List<string>? uploadModeLocalRefErrors = null;

        foreach (var entry in refs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var r = entry.Reference;

            if (entry.InlineBody is not null)
            {
                // Inline definition: resolved without filesystem or catalog.
                // Apply the same input substitution as a within-package body
                // so connector configs / other interpolated fields land
                // concrete values for the activator.
                var content = SubstituteInputs(entry.InlineBody, inputSchema, inputValues);
                result.Add(new RefResolution(r, new ResolvedArtefact
                {
                    Name = r.ArtefactName,
                    SourcePackage = null,
                    Kind = r.Kind,
                    ResolvedPath = null,
                    Content = content,
                }));
                continue;
            }

            if (!r.IsCrossPackage && string.IsNullOrEmpty(packageRoot))
            {
                // Upload mode: accumulate local ref errors; do not attempt resolution.
                uploadModeLocalRefErrors ??= [];
                uploadModeLocalRefErrors.Add($"{r.Kind.ToString().ToLowerInvariant()}: {r.ArtefactName}");
                continue;
            }

            ResolvedArtefact artefact;
            if (r.IsCrossPackage)
            {
                // Cross-package artefacts are resolved via the catalog provider;
                // their bodies are NOT substituted with this package's inputs —
                // substitution happens at that package's own install time.
                artefact = await ResolveCrossPackageAsync(r, catalogProvider, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                artefact = ResolveLocal(r, packageRoot!, inputSchema, inputValues);
            }

            result.Add(new RefResolution(r, artefact));
        }

        if (uploadModeLocalRefErrors is { Count: > 0 })
        {
            throw new PackageUploadHasLocalRefException(uploadModeLocalRefErrors);
        }

        return result;
    }

    private static ResolvedArtefact ResolveLocal(
        ArtefactReference r,
        string packageRoot,
        IReadOnlyList<PackageInputDefinition> inputSchema,
        IReadOnlyDictionary<string, string> inputValues)
    {
        var (subDir, extension) = r.Kind switch
        {
            ArtefactKind.Unit => ("units", ".yaml"),
            ArtefactKind.Agent => ("agents", ".yaml"),
            ArtefactKind.Skill => ("skills", ".md"),
            ArtefactKind.Workflow => ("workflows", ""),
            _ => throw new ArgumentOutOfRangeException()
        };

        string resolvedPath;
        string? content = null;

        if (r.Kind == ArtefactKind.Workflow)
        {
            resolvedPath = Path.Combine(packageRoot, subDir, r.ArtefactName);
            if (!Directory.Exists(resolvedPath))
            {
                throw new PackageReferenceNotFoundException(
                    r.RawValue,
                    $"Expected workflow directory at '{resolvedPath}'.");
            }
        }
        else
        {
            resolvedPath = Path.Combine(packageRoot, subDir, r.ArtefactName + extension);
            if (!File.Exists(resolvedPath))
            {
                // Try .yml variant.
                if (r.Kind is ArtefactKind.Unit or ArtefactKind.Agent)
                {
                    var yml = Path.Combine(packageRoot, subDir, r.ArtefactName + ".yml");
                    if (File.Exists(yml))
                    {
                        resolvedPath = yml;
                    }
                    else
                    {
                        throw new PackageReferenceNotFoundException(
                            r.RawValue,
                            $"Expected '{r.Kind.ToString().ToLowerInvariant()}' file at '{resolvedPath}'.");
                    }
                }
                else
                {
                    throw new PackageReferenceNotFoundException(
                        r.RawValue,
                        $"Expected '{r.Kind.ToString().ToLowerInvariant()}' file at '{resolvedPath}'.");
                }
            }

            var rawContent = File.ReadAllText(resolvedPath);

            // Apply ${{ inputs.* }} substitution to within-package artefact
            // bodies using the same schema and values as the root package.yaml.
            // This ensures connector configs and other fields in sub-unit YAMLs
            // carry concrete values, not literal expression strings, when the
            // resolved artefact reaches the activator.
            content = SubstituteInputs(rawContent, inputSchema, inputValues);
        }

        return new ResolvedArtefact
        {
            Name = r.ArtefactName,
            SourcePackage = null,
            Kind = r.Kind,
            ResolvedPath = resolvedPath,
            Content = content,
        };
    }

    private static async Task<ResolvedArtefact> ResolveCrossPackageAsync(
        ArtefactReference r,
        IPackageCatalogProvider? catalogProvider,
        CancellationToken cancellationToken)
    {
        if (catalogProvider is null)
        {
            throw new PackageReferenceNotFoundException(
                r.RawValue,
                $"Cross-package reference '{r.RawValue}' cannot be resolved: no catalog provider is configured.");
        }

        var content = await catalogProvider.LoadArtefactYamlAsync(
            r.PackageName!, r.Kind, r.ArtefactName, cancellationToken).ConfigureAwait(false);

        if (content is null)
        {
            // Try to check whether the package itself exists to give a better error.
            var packageExists = await catalogProvider.PackageExistsAsync(
                r.PackageName!, cancellationToken).ConfigureAwait(false);

            if (!packageExists)
            {
                throw new PackageReferenceNotFoundException(
                    r.RawValue,
                    $"Package '{r.PackageName}' was not found in the catalog.");
            }

            throw new PackageReferenceNotFoundException(
                r.RawValue,
                $"Artefact '{r.ArtefactName}' ({r.Kind}) was not found in package '{r.PackageName}'.");
        }

        // Cross-package artefacts must be self-contained: each install is
        // independent — the consuming package doesn't know the referenced
        // package's input schema, and prior installs are not reused. Any
        // ${{ inputs.* }} expression in the catalog body is therefore
        // unresolvable and indicates a broken artefact definition.
        if (InputInterpolationPattern.IsMatch(content))
        {
            throw new CrossPackageArtefactNotSelfContainedException(r.RawValue);
        }

        return new ResolvedArtefact
        {
            Name = r.ArtefactName,
            SourcePackage = r.PackageName,
            Kind = r.Kind,
            ResolvedPath = null,
            Content = content,
        };
    }

    // ---- Cycle detection -----------------------------------------------

    private static void DetectCycles(List<RefResolution> resolved)
    {
        // Build a name → content map so we can parse sub-unit references
        // within resolved unit manifests and detect self-referential loops.
        // For the package level: detect if any reference chain A → B → C → A
        // using artefact names as nodes.
        //
        // For v0.1 the graph is the package-level flat list. Cycles across the
        // within-package sub-unit references would require parsing each unit's
        // members — that is deeper than the package manifest layer. We detect
        // the simple case: the same artefact appearing in a circular fashion
        // at the package level (i.e., duplicate references that would already
        // be caught by uniqueness check). The ADR's DFS cycle detection note
        // refers to sub-unit nesting: unitA.members → unitB, unitB.members →
        // unitA. We implement that here for resolved units.

        var unitContentByName = resolved
            .Where(r => r.Reference.Kind == ArtefactKind.Unit && r.Artefact.Content is not null)
            .ToDictionary(
                r => r.Artefact.Name,
                r => r.Artefact.Content!,
                StringComparer.OrdinalIgnoreCase);

        // Parse sub-unit references from each unit manifest to build the graph.
        var graph = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, content) in unitContentByName)
        {
            graph[name] = ExtractSubUnitReferences(content);
        }

        // DFS cycle detection.
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

    private static List<string> ExtractSubUnitReferences(string unitYaml)
    {
        // We only look at unit members that are sub-units (unit: xxx) to
        // detect cross-unit cycles at the within-package level.
        //
        // Unit files have the format:
        //   unit:
        //     name: ...
        //     members:
        //       - unit: other-unit
        // We deserialize via ManifestDocument (same as ManifestParser) so the
        // outer `unit:` wrapper is handled correctly.
        var refs = new List<string>();
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();
            var doc = deserializer.Deserialize<ManifestDocument>(unitYaml);
            var manifest = doc?.Unit;
            if (manifest?.Members is { Count: > 0 })
            {
                foreach (var m in manifest.Members)
                {
                    if (!string.IsNullOrWhiteSpace(m.Unit))
                    {
                        // Bare name only — cross-package units are not in the graph.
                        var r = ArtefactReference.Parse(m.Unit, ArtefactKind.Unit);
                        if (!r.IsCrossPackage)
                        {
                            refs.Add(r.ArtefactName);
                        }
                    }
                }
            }
        }
        catch
        {
            // If we can't parse the member list, skip cycle detection for this node.
        }
        return refs;
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
                    // Cycle found — extract the cycle portion of the stack.
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

    // ---- Helpers --------------------------------------------------------

    private static void ValidateRequiredFields(PackageManifest doc)
    {
        if (string.IsNullOrWhiteSpace(doc.Kind))
        {
            throw new PackageParseException("Package manifest is missing the required 'kind' field.");
        }

        _ = ParseKind(doc.Kind); // validates the value.

        if (doc.Metadata is null || string.IsNullOrWhiteSpace(doc.Metadata.Name))
        {
            throw new PackageParseException("Package manifest is missing the required 'metadata.name' field.");
        }
    }

    private static PackageKind ParseKind(string kindStr) => kindStr switch
    {
        "UnitPackage" => PackageKind.UnitPackage,
        "AgentPackage" => PackageKind.AgentPackage,
        _ => throw new PackageParseException(
            $"Unknown package kind '{kindStr}'. Expected 'UnitPackage' or 'AgentPackage'.")
    };

    private static IReadOnlyDictionary<string, string> BuildFinalInputValues(
        IReadOnlyList<PackageInputDefinition> schema,
        IReadOnlyDictionary<string, string> supplied)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var def in schema)
        {
            if (string.IsNullOrWhiteSpace(def.Name))
            {
                continue;
            }

            if (supplied.TryGetValue(def.Name, out var v))
            {
                result[def.Name] = def.Secret
                    ? (v.StartsWith("secret://", StringComparison.Ordinal) ? v : $"secret://{v}")
                    : v;
            }
            else if (def.Default is not null)
            {
                result[def.Name] = def.Default;
            }
        }
        return result;
    }

    private static IDeserializer BuildDeserializer()
        => new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .WithTypeConverter(new InlineArtefactDefinitionYamlConverter())
            .IgnoreUnmatchedProperties()
            .Build();
}