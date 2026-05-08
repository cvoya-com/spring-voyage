// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest.Validation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

/// <summary>
/// Offline package validator for <c>spring package validate</c> (#1680).
/// Walks an <see cref="IPackageSource"/> rooted at a package directory and
/// reports schema, required-field, cross-reference, and connector-slug
/// findings without contacting a running platform. Designed for the CI gate
/// that prevents the in-tree packages from drifting out of "installable"
/// state and for the operator's local pre-publish check.
/// </summary>
/// <remarks>
/// <para>The validator runs in five passes (each independent so a single
/// failing file does not abort the whole run):</para>
/// <list type="number">
///   <item><description>Parse <c>package.yaml</c> via
///   <see cref="PackageManifestParser.ParseRaw"/>.</description></item>
///   <item><description>Parse every <c>units/*.yaml</c> via
///   <see cref="ManifestParser.Parse"/> and check
///   <c>execution.image</c>.</description></item>
///   <item><description>Parse every <c>agents/*.yaml</c> via a tolerant local
///   YamlDotNet shape and check <c>ai.model</c>.</description></item>
///   <item><description>Walk every YAML file (package + units + agents) for
///   <c>${{ inputs.&lt;name&gt; }}</c> tokens and confirm each name appears
///   in <c>package.yaml</c>'s <c>inputs:</c>.</description></item>
///   <item><description>For every unit YAML, verify each
///   <c>members[].agent</c> / <c>members[].unit</c> reference resolves to a
///   sibling agent / unit file (skipping cross-package Guid references) and
///   each <c>connectors[].type</c> slug is one of the v0.1 known set.</description></item>
/// </list>
/// </remarks>
public static class PackageValidator
{
    /// <summary>
    /// Known connector type slugs in v0.1. Hard-coded snapshot — see #1680
    /// for the rationale (CI gate beats a runtime probe). When connectors
    /// are added, append the slug here in the same PR that ships the
    /// connector.
    /// </summary>
    public static readonly IReadOnlyList<string> KnownConnectorSlugs = new[]
    {
        "github",
        "arxiv",
        "web-search",
    };

    private static readonly Regex InputInterpolationPattern =
        new(@"\$\{\{\s*inputs\.([A-Za-z_][A-Za-z0-9_]*)\s*\}\}", RegexOptions.Compiled);

    // 32-char no-dash hex (or any Guid.TryParse-accepted form). We only need
    // the no-dash 32-hex form for cross-package member refs (matches the
    // production manifest grammar — see ManifestParser.ValidateUnitMemberGrammar).
    private static readonly Regex CrossPackageGuidPattern =
        new("^[0-9a-fA-F]{32}$", RegexOptions.Compiled);

    /// <summary>
    /// Validates the package rooted at <paramref name="source"/>.
    /// </summary>
    /// <param name="source">The package source. For v0.1 always a <see cref="DirectoryPackageSource"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A <see cref="PackageValidationResult"/> with every visited file and any diagnostics.</returns>
    public static async Task<PackageValidationResult> ValidateAsync(
        IPackageSource source,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var diagnostics = new List<PackageValidationDiagnostic>();
        var visitedFiles = new List<string>();

        // ── package.yaml ─────────────────────────────────────────────────────
        const string packageYamlPath = "package.yaml";
        visitedFiles.Add(packageYamlPath);

        if (!source.FileExists(packageYamlPath))
        {
            diagnostics.Add(new PackageValidationDiagnostic(
                packageYamlPath,
                PackageValidationSeverity.Error,
                "package-yaml-missing",
                "Package root is missing 'package.yaml'."));
            return new PackageValidationResult
            {
                Files = visitedFiles,
                Diagnostics = diagnostics,
            };
        }

        var packageYaml = await source.ReadTextAsync(packageYamlPath, ct).ConfigureAwait(false);

        PackageManifest? packageManifest = null;
        try
        {
            packageManifest = PackageManifestParser.ParseRaw(packageYaml);
        }
        catch (PackageParseException ex)
        {
            diagnostics.Add(new PackageValidationDiagnostic(
                packageYamlPath,
                PackageValidationSeverity.Error,
                "package-parse",
                ex.Message));
        }

        // ADR-0037 D2: package-level `inputs:` is removed; any
        // `${{ inputs.* }}` expression is now invalid regardless of
        // where it appears.
        ValidateInputInterpolations(packageYaml, packageYamlPath, diagnostics);

        // ── units/*.yaml ─────────────────────────────────────────────────────
        var unitFiles = source.EnumerateFiles("units", "*.yaml").ToList();
        unitFiles.AddRange(source.EnumerateFiles("units", "*.yml"));
        var unitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var unitFile in unitFiles)
        {
            // Filename without extension is the bare local symbol referenced
            // from members[].unit — see ResolveLocal in PackageManifestParser.
            var name = StripExtension(System.IO.Path.GetFileName(unitFile));
            unitNames.Add(name);
        }

        // ── agents/*.yaml ────────────────────────────────────────────────────
        var agentFiles = source.EnumerateFiles("agents", "*.yaml").ToList();
        agentFiles.AddRange(source.EnumerateFiles("agents", "*.yml"));
        var agentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agentFile in agentFiles)
        {
            var name = StripExtension(System.IO.Path.GetFileName(agentFile));
            agentNames.Add(name);
        }

        // Walk units. Each file: schema parse + execution.image required +
        // member refs resolve + connector slugs known + input interpolations.
        foreach (var unitFile in unitFiles)
        {
            visitedFiles.Add(unitFile);
            ct.ThrowIfCancellationRequested();
            var unitYaml = await source.ReadTextAsync(unitFile, ct).ConfigureAwait(false);

            UnitManifest? unit = null;
            try
            {
                unit = ManifestParser.Parse(unitYaml);
            }
            catch (ManifestParseException ex)
            {
                diagnostics.Add(new PackageValidationDiagnostic(
                    unitFile,
                    PackageValidationSeverity.Error,
                    "unit-parse",
                    ex.Message));
            }

            ValidateInputInterpolations(unitYaml, unitFile, diagnostics);

            if (unit is null)
            {
                continue;
            }

            // execution.image is required: every unit declared in v0.1
            // packages needs an image so the dispatcher can launch its
            // container. (Unit-level execution is the inheritance source for
            // member agents — see UnitManifest.Execution remarks.)
            //
            // #1679: a package-level `execution.image` default may cover
            // member units that don't declare their own. Skip the per-unit
            // check when the package provides an image AND this unit is
            // in the inheritance scope. The strict inherit-shape parsing
            // is owned by PackageManifestParser; if it's malformed, the
            // parse error already lands on the package file.
            var packageImage = packageManifest?.Execution?.Image;
            var inheritsPackageImage =
                !string.IsNullOrWhiteSpace(packageImage)
                && UnitInheritsPackageExecution(unit.Name, packageManifest!.Execution!.Inherit);

            if (string.IsNullOrWhiteSpace(unit.Execution?.Image) && !inheritsPackageImage)
            {
                diagnostics.Add(new PackageValidationDiagnostic(
                    unitFile,
                    PackageValidationSeverity.Error,
                    "unit-missing-image",
                    $"unit '{unit.Name ?? "<unnamed>"}': execution.image is required."));
            }

            // members[].agent / members[].unit must resolve in-package.
            // Cross-package Guid refs are accepted unconditionally (the
            // catalog resolves them at install time).
            if (unit.Members is { Count: > 0 })
            {
                for (var i = 0; i < unit.Members.Count; i++)
                {
                    var member = unit.Members[i];
                    if (!string.IsNullOrWhiteSpace(member.Agent))
                    {
                        if (!IsCrossPackageGuid(member.Agent) && !agentNames.Contains(member.Agent))
                        {
                            diagnostics.Add(new PackageValidationDiagnostic(
                                unitFile,
                                PackageValidationSeverity.Error,
                                "unit-member-agent-not-found",
                                $"unit '{unit.Name ?? "<unnamed>"}': members[{i}].agent '{member.Agent}' " +
                                $"does not match any file in agents/ (expected agents/{member.Agent}.yaml)."));
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(member.Unit))
                    {
                        if (!IsCrossPackageGuid(member.Unit) && !unitNames.Contains(member.Unit))
                        {
                            diagnostics.Add(new PackageValidationDiagnostic(
                                unitFile,
                                PackageValidationSeverity.Error,
                                "unit-member-unit-not-found",
                                $"unit '{unit.Name ?? "<unnamed>"}': members[{i}].unit '{member.Unit}' " +
                                $"does not match any file in units/ (expected units/{member.Unit}.yaml)."));
                        }
                    }
                }
            }

            // requires[].connector must be a known v0.1 slug. Unknown slug is a
            // warning by default (the platform will reject it at install
            // time, but we surface it earlier); --strict promotes it to an
            // error in the CLI layer. ADR-0037 decision 3 — the per-unit
            // requirements block is `requires:` and each entry is a
            // single-key map keyed by requirement type.
            if (unit.Requires is { Count: > 0 })
            {
                for (var i = 0; i < unit.Requires.Count; i++)
                {
                    var req = unit.Requires[i];
                    if (req.Type != RequirementType.Connector)
                    {
                        continue;
                    }
                    var slug = req.Identifier;
                    if (string.IsNullOrWhiteSpace(slug))
                    {
                        diagnostics.Add(new PackageValidationDiagnostic(
                            unitFile,
                            PackageValidationSeverity.Error,
                            "requires-missing-identifier",
                            $"unit '{unit.Name ?? "<unnamed>"}': requires[{i}].connector is required."));
                        continue;
                    }
                    if (!KnownConnectorSlugs.Contains(slug, StringComparer.OrdinalIgnoreCase))
                    {
                        diagnostics.Add(new PackageValidationDiagnostic(
                            unitFile,
                            PackageValidationSeverity.Warning,
                            "connector-unknown-slug",
                            $"unit '{unit.Name ?? "<unnamed>"}': requires[{i}].connector '{slug}' is not a " +
                            $"known connector slug (known: {string.Join(", ", KnownConnectorSlugs)})."));
                    }
                }
            }
        }

        // Walk agents. ADR-0037 D1: agent YAMLs are kind-discriminated
        // top-level documents (apiVersion / kind: Agent / name / description)
        // — the legacy `agent:` wrapper is gone. Validate headers, then
        // ai.model, then input interpolations.
        foreach (var agentFile in agentFiles)
        {
            visitedFiles.Add(agentFile);
            ct.ThrowIfCancellationRequested();
            var agentYaml = await source.ReadTextAsync(agentFile, ct).ConfigureAwait(false);

            AgentDocument? doc = null;
            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                doc = deserializer.Deserialize<AgentDocument>(agentYaml);
            }
            catch (YamlDotNet.Core.YamlException ex)
            {
                diagnostics.Add(new PackageValidationDiagnostic(
                    agentFile,
                    PackageValidationSeverity.Error,
                    "agent-parse",
                    $"Invalid YAML: {ex.Message}"));
            }

            ValidateInputInterpolations(agentYaml, agentFile, diagnostics);

            if (doc is null)
            {
                continue;
            }

            // ADR-0037 D6: detect legacy `agent:` wrapper and surface a
            // precise migration hint.
            if (doc.LegacyAgentWrapper is not null)
            {
                diagnostics.Add(new PackageValidationDiagnostic(
                    agentFile,
                    PackageValidationSeverity.Error,
                    "agent-legacy-wrapper",
                    "LegacyArtefactWrapper: agent YAML wraps the body in an 'agent:' key. " +
                    "ADR-0037 decision 1 — drop the wrapping 'agent:' key; hoist the body to the " +
                    "top level with apiVersion: spring.voyage/v1, kind: Agent, name, description."));
                continue;
            }

            // ADR-0038: reject legacy ai-block shapes with a precise
            // migration hint. Mirrors the unit-side detection in
            // ManifestParser.DetectLegacyAiShapes so unit and agent
            // YAMLs share one rejection rule.
            try
            {
                ManifestParser.DetectLegacyAiShapes(agentYaml);
            }
            catch (ManifestParseException ex)
            {
                diagnostics.Add(new PackageValidationDiagnostic(
                    agentFile,
                    PackageValidationSeverity.Error,
                    "agent-legacy-ai-shape",
                    ex.Message));
                continue;
            }

            // ADR-0039 § 9: reject `containerRuntime:` (root or under
            // `execution:`) — the container runtime is platform
            // configuration, not a per-agent field. Same shared
            // detector the unit-side parser uses.
            try
            {
                ManifestParser.DetectLegacyContainerRuntime(agentYaml);
            }
            catch (ManifestParseException ex)
            {
                diagnostics.Add(new PackageValidationDiagnostic(
                    agentFile,
                    PackageValidationSeverity.Error,
                    "agent-legacy-container-runtime",
                    ex.Message));
                continue;
            }

            if (string.IsNullOrWhiteSpace(doc.ApiVersion))
            {
                diagnostics.Add(new PackageValidationDiagnostic(
                    agentFile,
                    PackageValidationSeverity.Error,
                    "agent-missing-apiversion",
                    "MissingApiVersion: every artefact YAML declares apiVersion: spring.voyage/v1 (ADR-0037 decision 1)."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(doc.Kind))
            {
                diagnostics.Add(new PackageValidationDiagnostic(
                    agentFile,
                    PackageValidationSeverity.Error,
                    "agent-missing-kind",
                    "MissingKind: every artefact YAML declares kind: Agent (ADR-0037 decision 1)."));
                continue;
            }

            if (!string.Equals(doc.Kind.Trim(), "Agent", StringComparison.Ordinal))
            {
                diagnostics.Add(new PackageValidationDiagnostic(
                    agentFile,
                    PackageValidationSeverity.Error,
                    "agent-wrong-kind",
                    $"Agent YAML declares kind: '{doc.Kind}' but expected 'Agent'."));
                continue;
            }

            // ADR-0038: ai.model is the structured {provider, id} object;
            // require both fields so the activator has enough to bind to
            // a model entry in the catalogue.
            var modelId = doc.Ai?.Model?.Id;
            var modelProvider = doc.Ai?.Model?.Provider;
            if (string.IsNullOrWhiteSpace(modelId) || string.IsNullOrWhiteSpace(modelProvider))
            {
                diagnostics.Add(new PackageValidationDiagnostic(
                    agentFile,
                    PackageValidationSeverity.Error,
                    "agent-missing-model",
                    $"agent '{doc.Id ?? doc.Name ?? "<unnamed>"}': ai.model is required " +
                    "as a structured {provider, id} object (ADR-0038)."));
            }
        }

        return new PackageValidationResult
        {
            Files = visitedFiles,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// ADR-0037 D2: package-level <c>inputs:</c> was removed. Any
    /// <c>${{ inputs.* }}</c> expression in any YAML file is now an
    /// authoring error.
    /// </summary>
    private static void ValidateInputInterpolations(
        string yaml,
        string filePath,
        List<PackageValidationDiagnostic> diagnostics)
    {
        var seenUndeclared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in InputInterpolationPattern.Matches(yaml))
        {
            var name = m.Groups[1].Value;
            if (!seenUndeclared.Add(name))
            {
                continue;
            }
            diagnostics.Add(new PackageValidationDiagnostic(
                filePath,
                PackageValidationSeverity.Error,
                "input-expression-removed",
                $"references input '{name}' via '${{{{ inputs.{name} }}}}'. " +
                "ADR-0037 decision 2 removed the inputs: schema; remove the expression " +
                "or move the value into a per-artefact 'requires:' binding."));
        }
    }

    private static string StripExtension(string fileName)
    {
        var dot = fileName.LastIndexOf('.');
        return dot < 0 ? fileName : fileName[..dot];
    }

    private static bool IsCrossPackageGuid(string symbol)
    {
        // Bare 32-char no-dash hex matches the production manifest grammar.
        // Also accept any Guid-parseable form for defensiveness.
        return CrossPackageGuidPattern.IsMatch(symbol) || Guid.TryParse(symbol, out _);
    }

    /// <summary>
    /// Returns true when <paramref name="unitName"/> falls within the
    /// package-level <c>execution.inherit:</c> scope (#1679). The raw
    /// shape comes straight from <see cref="PackageExecutionManifest.Inherit"/>
    /// and mirrors what <see cref="PackageManifestParser"/> accepts:
    /// <c>null</c> or the literal scalar <c>"all"</c> ⇒ every member
    /// inherits; a YAML sequence ⇒ only the named members. Malformed
    /// shapes (a different scalar, a mapping, a non-string sequence
    /// entry) fall through to "applies = true" so the operator's
    /// primary error remains the parse error rather than a spurious
    /// missing-image complaint.
    /// </summary>
    private static bool UnitInheritsPackageExecution(string? unitName, object? rawInherit)
    {
        if (string.IsNullOrWhiteSpace(unitName))
        {
            return false;
        }
        if (rawInherit is null)
        {
            return true;
        }
        if (rawInherit is string scalar)
        {
            return string.Equals(scalar.Trim(), "all", StringComparison.OrdinalIgnoreCase);
        }
        if (rawInherit is System.Collections.IEnumerable sequence)
        {
            foreach (var item in sequence)
            {
                if (item is string name
                    && string.Equals(name.Trim(), unitName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }
        return true;
    }

    // ── local YAML shapes for tolerant agent parsing ──────────────────────
    //
    // ADR-0037 D1: agent YAMLs are kind-discriminated top-level documents
    // (apiVersion / kind: Agent / name / description / id / ai / …). The
    // wrapping `agent:` key from the pre-ADR-0037 grammar is captured
    // separately so the validator can surface a precise
    // LegacyArtefactWrapper migration hint.

    private sealed class AgentDocument
    {
        [YamlDotNet.Serialization.YamlMember(Alias = "apiVersion")]
        public string? ApiVersion { get; set; }

        [YamlDotNet.Serialization.YamlMember(Alias = "kind")]
        public string? Kind { get; set; }

        [YamlDotNet.Serialization.YamlMember(Alias = "name")]
        public string? Name { get; set; }

        [YamlDotNet.Serialization.YamlMember(Alias = "description")]
        public string? Description { get; set; }

        [YamlDotNet.Serialization.YamlMember(Alias = "id")]
        public string? Id { get; set; }

        [YamlDotNet.Serialization.YamlMember(Alias = "ai")]
        public AgentAiDoc? Ai { get; set; }

        [YamlDotNet.Serialization.YamlMember(Alias = "agent")]
        public object? LegacyAgentWrapper { get; set; }
    }

    private sealed class AgentAiDoc
    {
        [YamlDotNet.Serialization.YamlMember(Alias = "runtime")]
        public string? Runtime { get; set; }

        [YamlDotNet.Serialization.YamlMember(Alias = "model")]
        public AgentAiModelDoc? Model { get; set; }
    }

    private sealed class AgentAiModelDoc
    {
        [YamlDotNet.Serialization.YamlMember(Alias = "provider")]
        public string? Provider { get; set; }

        [YamlDotNet.Serialization.YamlMember(Alias = "id")]
        public string? Id { get; set; }
    }
}