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
/// 10, and 14 plus #1718 items 1+2 (drop <c>kind:</c>, unify artefact
/// declarations under a single <c>content:</c> list):
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
        catch (PackageParseException)
        {
            // Bubble through — converters may already throw the right
            // type, e.g. legacy-shape rejection in BuildDeserializer's
            // path. (Today there is no such converter; preserved for
            // forward symmetry.)
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
        ValidatePackageGrammar(doc);
        return doc;
    }

    /// <summary>
    /// Normalises the package-level <c>connectors:</c> block (#1670). The
    /// raw YAML accepts <c>inherit: all</c> (default), <c>inherit: [unit-a,
    /// unit-b]</c>, or <c>inherit:</c> absent. Surface the parsed shape on
    /// the typed <see cref="RequiredConnector.InheritAll"/> /
    /// <see cref="RequiredConnector.InheritUnits"/> slots so the install
    /// pipeline never has to re-walk the raw YAML.
    /// </summary>
    private static void NormaliseConnectorBlock(PackageManifest doc)
    {
        if (doc.Connectors is not { Count: > 0 } connectors)
        {
            return;
        }

        for (var i = 0; i < connectors.Count; i++)
        {
            var entry = connectors[i];
            if (entry is null)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(entry.Type))
            {
                throw new PackageParseException(
                    $"connectors[{i}]: 'type' is required.");
            }

            switch (entry.InheritRaw)
            {
                case null:
                    entry.InheritAll = true;
                    entry.InheritUnits = null;
                    break;
                case string s:
                    if (!string.Equals(s.Trim(), "all", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new PackageParseException(
                            $"connectors[{i}].inherit: only the literal string 'all' or a sequence of unit names is accepted, got '{s}'.");
                    }
                    entry.InheritAll = true;
                    entry.InheritUnits = null;
                    break;
                case System.Collections.IEnumerable seq when seq is not string:
                    var names = new List<string>();
                    foreach (var item in seq)
                    {
                        if (item is null) continue;
                        var name = item.ToString();
                        if (string.IsNullOrWhiteSpace(name)) continue;
                        names.Add(name.Trim());
                    }
                    entry.InheritAll = false;
                    entry.InheritUnits = names;
                    break;
                default:
                    throw new PackageParseException(
                        $"connectors[{i}].inherit: unsupported shape '{entry.InheritRaw.GetType().Name}'. " +
                        "Expected the literal 'all' (default) or a list of member-unit names.");
            }
        }
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
        if (doc.Content is not { Count: > 0 } content)
        {
            return;
        }

        for (var i = 0; i < content.Count; i++)
        {
            var entry = content[i];
            if (entry?.Definition is { IsInline: false } slot
                && !string.IsNullOrWhiteSpace(slot.Reference))
            {
                LocalSymbolValidator.RejectPathStyleReference(
                    slot.Reference, $"content[{i}].{KindKey(entry.Kind)}", GrammarLayer.PackageManifest);
            }
        }
    }

    /// <summary>
    /// ADR-0037 decision 6: reject every legacy-shape signal with a precise
    /// migration hint. Covers items 1+2 from #1718 (already shipped in PR #1719)
    /// plus the per-artefact decomposition from ADR-0037.
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

        // ADR-0037 decision 2: metadata: nesting is removed; name and
        // description live at the top level.
        if (doc.Metadata is not null)
        {
            throw new PackageParseException(
                "LegacyMetadataNesting: 'metadata:' nesting is removed in ADR-0037. " +
                "Hoist 'name', 'description', and 'readme' to the top level of package.yaml.");
        }

        // ADR-0037 decision 2: inputs: is removed; connector-binding
        // parameters move into per-artefact requires:.
        if (doc.RawInputs is not null)
        {
            throw new PackageParseException(
                "LegacyInputsField: 'inputs:' is removed in ADR-0037. " +
                "Move connector-binding parameters into per-artefact 'requires:' blocks; " +
                "behaviour parameters move to per-unit 'policies:'.");
        }

        // ADR-0037 decision 2: package-level connectors: is removed.
        if (doc.RawConnectors is not null)
        {
            throw new PackageParseException(
                "LegacyPackageConnectorsField: package-level 'connectors:' is removed in ADR-0037. " +
                "Declare requirements on per-artefact YAMLs as 'requires: [{ connector: <slug> }]'. " +
                "The package's effective requirement set is the union of every artefact's requires.");
        }

        // #1718 item 2: flat artefact lists are gone (already shipped
        // pre-ADR-0037; kept here for the comprehensive D6 migration
        // surface).
        var legacyKeys = new[] { "unit", "agent", "subUnits", "skills", "workflows" };
        foreach (var key in legacyKeys)
        {
            if (TopLevelKeyPresent(yamlText, key))
            {
                throw new PackageParseException(
                    $"Package manifest declares the obsolete top-level '{key}:' field. " +
                    "v0.1 declares all bundled artefacts under a single 'content:' list " +
                    "(#1718 item 2). Move the artefact reference into 'content:' as " +
                    $"'- {SuggestedContentKey(key)}: <name>'. " +
                    "Sub-units of an umbrella unit are discovered automatically from " +
                    "the umbrella's 'members:' list — they no longer need to be " +
                    "enumerated under 'subUnits:'.");
            }
        }
    }

    private static string SuggestedContentKey(string legacyKey) => legacyKey switch
    {
        "subUnits" => "unit",
        "skills" => "skill",
        "workflows" => "workflow",
        _ => legacyKey,
    };

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
            // Strip any trailing CR (CRLF endings).
            var line = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
            if (line.Length == 0) continue;
            // Top-level keys live at column 0.
            if (line[0] is ' ' or '\t' or '#') continue;
            if (line.StartsWith(prefix, StringComparison.Ordinal))
            {
                // Exactly the key followed by ':' (no extra alphanumeric
                // chars — `unit:` matches but `unitTemplate:` does not).
                if (line.Length == prefix.Length || !IsKeyChar(line[prefix.Length - 1])
                    || line[prefix.Length] is ' ' or '\t' or '\0' or '\r')
                {
                    // Re-check: line[prefix.Length-1] is ':' so the key
                    // boundary is well-formed; the next char (if any) must
                    // be whitespace or end-of-line.
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

        // Step 6: Validate name uniqueness across the explicitly-declared
        // top-level entries. (Member-discovered descendants are deduplicated
        // by the resolver below; they do not need to be unique against
        // top-level entries because top-level wins — re-declaration just
        // adds a redundant explicit reference, which is fine.)
        ValidateNameUniqueness(allRefs.Select(e => e.Reference).ToList());

        // Step 7: Resolve all references (passing input schema + values so
        // within-package local artefact bodies get the same substitution pass).
        // After resolving the top-level entries, recursively descend into
        // each resolved unit's `members:` list to discover sub-units / agents
        // that the package author no longer enumerates explicitly (#1718
        // item 2). Every reachable artefact ends up in `resolved` so the
        // install pipeline can mint a Guid per name and the activator can
        // rewrite member references off a fully-populated symbol map.
        var resolved = await ResolveReferencesWithDescendantsAsync(
            allRefs, packageRoot, manifest.Inputs ?? [], inputValues,
            catalogProvider, cancellationToken).ConfigureAwait(false);

        // Step 8: Detect cycles.
        DetectCycles(resolved);

        // Step 8.5: validate the package-level connectors block against the
        // resolved unit set (#1670) — `inherit: [unit]` lists must reference
        // real members, and a unit declaring `inherit: false` on a slug the
        // package doesn't declare is an error. Identical inherited entries
        // are downgraded to a parse-time *warning* and ignored at install
        // time.
        ValidateConnectorBlock(manifest, resolved);

        // #1718 item 1: package kind is computed from the parsed content,
        // not from a YAML scalar. Empty / unit-only / mixed shapes resolve
        // as UnitPackage; an exclusively-agent top-level resolves as
        // AgentPackage so the install pipeline's discriminator-driven
        // codepaths keep working unchanged.
        var kind = InferKind(manifest);
        var name = manifest.Name!;

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

        // ADR-0037 D3: compute the package-level requires union from each
        // artefact's per-artefact `requires:` block. The install pipeline
        // asks the operator for one binding per unique slug and applies it
        // to every artefact that declared it.
        var (requiredConnectorSlugs, connectorRequiresByArtefact) =
            ComputeRequiresUnion(units, agents, skills, workflows);

        return new ResolvedPackage
        {
            Name = name,
            Description = manifest.Description,
            Version = manifest.Version,
            Kind = kind,
            InputValues = finalInputValues,
            Units = units,
            Agents = agents,
            Skills = skills,
            Workflows = workflows,
            RequiredConnectorSlugs = requiredConnectorSlugs,
            ConnectorRequiresByArtefact = connectorRequiresByArtefact,
            Connectors = manifest.Connectors ?? new List<RequiredConnector>(),
        };
    }

    /// <summary>
    /// Computes the per-package union of artefact <c>requires:</c> blocks
    /// (ADR-0037 D3). For every contained artefact, deserialises its
    /// resolved YAML content and reads its <c>requires:</c> list, then
    /// dedupes by slug. Returns the union slug list plus a per-artefact
    /// map of which slugs each artefact declared so the install pipeline
    /// can inject the resolved binding into exactly the artefacts that
    /// asked for it.
    /// </summary>
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
        // Skills and workflows: typed view for SkillManifest/WorkflowManifest
        // also has Requires; for v0.1 the resolved content path for skills
        // is markdown (no Requires reachable here), so we conservatively
        // skip them. The portal/cli pipeline will still surface union
        // accurately for the unit/agent shapes that matter today.
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

        if (manifest.Content is not { Count: > 0 } content)
        {
            return refs;
        }

        for (var i = 0; i < content.Count; i++)
        {
            var entry = content[i];
            if (entry is null)
            {
                continue;
            }

            // Skill / workflow inline bodies are not part of the v0.1
            // grammar — reject them with an actionable message rather
            // than letting the activator try to feed an unwrapped
            // mapping into the skill loader.
            if (entry.Kind is ArtefactKind.Skill or ArtefactKind.Workflow
                && entry.Definition.IsInline)
            {
                throw new PackageParseException(
                    $"content[{i}].{KindKey(entry.Kind)}: inline definitions are not supported for skills " +
                    "or workflows. Use a bare reference (e.g. 'skill: my-skill').");
            }

            AddSlot(refs, entry.Definition, entry.Kind);
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
            // ADR-0037 decision 1: per-artefact YAMLs are kind-discriminated
            // top-level documents. The captured inline body already carries
            // its own apiVersion/kind/name headers (or should — the parser
            // surfaces the missing-headers error downstream), so the body is
            // emitted verbatim with no wrapping key.
            var inlineName = slot.InlineName ?? "<inline>";
            refs.Add(new ArtefactCollectEntry(
                new ArtefactReference(inlineName, PackageName: null, inlineName, kind),
                InlineBody: slot.InlineBody!));
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

    /// <summary>
    /// #1718 item 2: resolve top-level <see cref="PackageManifest.Content"/>
    /// entries, then recursively descend into each resolved unit's
    /// <c>members:</c> list to discover sub-units / agents that the
    /// package author no longer enumerates explicitly. Every reachable
    /// artefact ends up in the returned list so the install pipeline can
    /// pre-mint a Guid per name and the activator can rewrite each
    /// member reference off a fully-populated symbol map.
    /// </summary>
    /// <remarks>
    /// Discovered descendants are added to the resolution list only when
    /// they are not already present (top-level entries win). Cross-package
    /// member references (the no-dash 32-hex Guid form) are skipped — the
    /// referenced package owns their identity. Cycle detection still runs
    /// on the final graph in <see cref="DetectCycles"/>; the discovery
    /// loop itself terminates because each member name is added to the
    /// resolution map at most once.
    /// </remarks>
    private static async Task<List<RefResolution>> ResolveReferencesWithDescendantsAsync(
        List<ArtefactCollectEntry> topLevelRefs,
        string? packageRoot,
        IReadOnlyList<PackageInputDefinition> inputSchema,
        IReadOnlyDictionary<string, string> inputValues,
        IPackageCatalogProvider? catalogProvider,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveReferencesAsync(
            topLevelRefs, packageRoot, inputSchema, inputValues,
            catalogProvider, cancellationToken).ConfigureAwait(false);

        // Index resolved artefacts by `(kind, name)` so descendant lookup
        // is O(1) and we don't double-resolve a unit that is also listed
        // as a top-level content entry.
        var byKey = new Dictionary<string, RefResolution>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in resolved)
        {
            byKey[ArtefactKey(r.Reference.Kind, r.Reference.ArtefactName)] = r;
        }

        // BFS over the unit graph. Each iteration pulls a unit's `members:`
        // list and resolves any not-yet-seen members. A member that's
        // already a peer artefact (top-level or already discovered) is a
        // no-op; the cycle detector handles closed cycles separately.
        var queue = new Queue<RefResolution>(resolved.Where(r => r.Reference.Kind == ArtefactKind.Unit));

        while (queue.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = queue.Dequeue();
            if (node.Artefact.Content is null)
            {
                // Cross-package units may not have content (the catalog
                // provider returns the body, but we treat them as
                // self-contained — descendants live in the referenced
                // package's catalog, not in this batch).
                continue;
            }
            if (node.Reference.IsCrossPackage)
            {
                continue;
            }

            var members = ExtractMemberRefs(node.Artefact.Content);
            foreach (var (memberKind, memberRef) in members)
            {
                // Discovery descends into unit members only. Agent members
                // are activated through the existing
                // <see cref="DefaultPackageArtefactActivator"/> fall-back
                // path (per-unit creation auto-registers them); pulling
                // them up to <c>pkg.Agents</c> would route them through
                // the standalone-agent activation codepath, which expects
                // a directory-Guid identity rather than a bare slug.
                if (memberKind != ArtefactKind.Unit)
                {
                    continue;
                }

                var artefactRef = ArtefactReference.Parse(memberRef, memberKind);

                // Cross-package member: identity owned by the referenced
                // package; skip discovery.
                if (artefactRef.IsCrossPackage)
                {
                    continue;
                }

                var key = ArtefactKey(memberKind, artefactRef.ArtefactName);
                if (byKey.ContainsKey(key))
                {
                    continue;
                }

                ResolvedArtefact memberArtefact;
                try
                {
                    memberArtefact = ResolveLocal(artefactRef, packageRoot ?? string.Empty, inputSchema, inputValues);
                }
                catch (PackageReferenceNotFoundException)
                {
                    // A unit member that doesn't have a sibling YAML must
                    // either be in the directory (handled by the
                    // activator's directory fall-back) or be operator
                    // error. We surface it through the existing
                    // <see cref="UmbrellaMemberNotFoundException"/> path
                    // rather than failing the parse — keeps the shape of
                    // the error consistent with the pre-#1718 behaviour.
                    continue;
                }
                catch (ArgumentException)
                {
                    // packageRoot is empty (upload mode) and the member is
                    // a bare local reference. The top-level resolver will
                    // already have surfaced this as
                    // PackageUploadHasLocalRefException; we skip silently.
                    continue;
                }

                var newResolution = new RefResolution(artefactRef, memberArtefact);
                byKey[key] = newResolution;
                resolved.Add(newResolution);

                // Recurse into newly-discovered units so transitive
                // sub-units (umbrella → team → agent-group) all surface.
                queue.Enqueue(newResolution);
            }
        }

        return resolved;
    }

    private static string ArtefactKey(ArtefactKind kind, string name)
        => $"{kind}|{name.ToLowerInvariant()}";

    /// <summary>
    /// Pulls each <c>members[].unit</c> / <c>members[].agent</c> entry from
    /// a resolved unit YAML body (ADR-0037 decision 1 — kind-discriminated
    /// top-level documents).
    /// </summary>
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
                yield return (ArtefactKind.Unit, m.Unit!);
            }
            else if (!string.IsNullOrWhiteSpace(m.Agent))
            {
                yield return (ArtefactKind.Agent, m.Agent!);
            }
        }
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
        // package's input schema, and prior installs of the referenced package are
        // not reused. Any
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

    // ---- Connector block validation (#1670) ----------------------------

    /// <summary>
    /// Validates the package-level <c>connectors:</c> block against the
    /// resolved unit set. Rules (#1670):
    /// <list type="bullet">
    ///   <item><description>
    ///     Every entry on a package-level <c>inherit: [..]</c> list must
    ///     name a unit that exists in this package.
    ///   </description></item>
    ///   <item><description>
    ///     A unit's own <c>connectors:</c> entry that names a slug the
    ///     package does not declare is an error when that entry sets
    ///     <c>inherit: false</c> — opting out of an inheritance the package
    ///     never offered.
    ///   </description></item>
    /// </list>
    /// </summary>
    private static void ValidateConnectorBlock(
        PackageManifest manifest,
        List<RefResolution> resolved)
    {
        if (manifest.Connectors is not { Count: > 0 } pkgConnectors)
        {
            return;
        }

        var unitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in resolved.Where(r => r.Reference.Kind == ArtefactKind.Unit))
        {
            unitNames.Add(r.Artefact.Name);
        }

        for (var i = 0; i < pkgConnectors.Count; i++)
        {
            var entry = pkgConnectors[i];
            if (entry?.InheritUnits is not { Count: > 0 } list)
            {
                continue;
            }
            foreach (var unitName in list)
            {
                if (!unitNames.Contains(unitName))
                {
                    throw new PackageParseException(
                        $"connectors[{i}].inherit: unit '{unitName}' is not declared in this package.");
                }
            }
        }

        var declaredSlugs = new HashSet<string>(
            pkgConnectors.Where(c => !string.IsNullOrWhiteSpace(c?.Type)).Select(c => c!.Type!),
            StringComparer.OrdinalIgnoreCase);

        // ADR-0037 decision 3: package-level connector inheritance is gone.
        // The unit-level legacy connectors:/inherit:false opt-out path is
        // dead under the new schema — every artefact declares its own
        // requires:, the install pipeline injects bindings 1:1, and the
        // resolved unit graph never reaches this branch because
        // pkgConnectors is empty. Logic preserved as a no-op for the
        // transitional period; #1726 deletes ValidateConnectorBlock and
        // the legacy types entirely.
        _ = declaredSlugs;
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
        // Unit files (ADR-0037) are kind-discriminated top-level documents:
        //   apiVersion: spring.voyage/v1
        //   kind: Unit
        //   name: ...
        //   members:
        //     - unit: other-unit
        var refs = new List<string>();
        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(CamelCaseNamingConvention.Instance)
                .WithTypeConverter(new RequirementEntryYamlConverter())
                .IgnoreUnmatchedProperties()
                .Build();
            var manifest = deserializer.Deserialize<UnitManifest>(unitYaml);
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

    private static string KindKey(ArtefactKind kind) => kind switch
    {
        ArtefactKind.Unit => "unit",
        ArtefactKind.Agent => "agent",
        ArtefactKind.Skill => "skill",
        ArtefactKind.Workflow => "workflow",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// Computes the package kind from the parsed <see cref="PackageManifest.Content"/>
    /// list (#1718 item 1). A package whose only top-level entries are
    /// agents resolves as <see cref="PackageKind.AgentPackage"/>; every
    /// other shape (including the empty / units-only / mixed shapes)
    /// resolves as <see cref="PackageKind.UnitPackage"/>. Tests that key
    /// off the discriminator (e.g. <c>simple-agent-package</c>) keep
    /// working unchanged because their <c>content:</c> contains exactly
    /// one agent and no units.
    /// </summary>
    private static PackageKind InferKind(PackageManifest manifest)
    {
        if (manifest.Content is not { Count: > 0 } content)
        {
            return PackageKind.UnitPackage;
        }

        var hasUnit = content.Any(e => e?.Kind == ArtefactKind.Unit);
        var hasAgent = content.Any(e => e?.Kind == ArtefactKind.Agent);
        if (hasAgent && !hasUnit)
        {
            return PackageKind.AgentPackage;
        }
        return PackageKind.UnitPackage;
    }

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
            .WithTypeConverter(new ContentEntryYamlConverter())
            .WithTypeConverter(new InlineArtefactDefinitionYamlConverter())
            .WithTypeConverter(new RequirementEntryYamlConverter())
            .IgnoreUnmatchedProperties()
            .Build();
}