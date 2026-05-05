// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;

using YamlDotNet.Serialization;

/// <summary>
/// Discriminates the kind of package post-resolve. Computed by
/// <see cref="PackageManifestParser"/> from the parsed
/// <see cref="PackageManifest.Content"/> entries.
/// </summary>
/// <remarks>
/// A package whose <see cref="PackageManifest.Content"/> entries are all
/// agents (and which has no top-level units) resolves as
/// <see cref="AgentPackage"/>. Every other shape — including the empty
/// shape used by upload-mode minimal packages — resolves as
/// <see cref="UnitPackage"/>. This is purely a downstream convenience for
/// the install-pipeline tests; production code keys off the per-artefact
/// <see cref="ArtefactKind"/> instead.
/// </remarks>
public enum PackageKind
{
    /// <summary>The package bundles a unit (a composite of agents and/or sub-units).</summary>
    UnitPackage,

    /// <summary>The package bundles a single agent.</summary>
    AgentPackage,
}

/// <summary>
/// Root document for a <c>package.yaml</c> manifest. Parsed by
/// <see cref="PackageManifestParser"/>.
/// </summary>
/// <remarks>
/// <para>
/// The package YAML shape under ADR-0037 (decision 2):
/// </para>
/// <code>
/// apiVersion: spring.voyage/v1
/// kind: Package
/// name: my-package
/// description: Single-line summary.
/// readme: README.md            # optional; relative path to a markdown file
/// version: 1.0.0
/// content:
///   - unit: sv-oss-design                  # bare = local symbol → ./units/sv-oss-design.yaml
///   - agent: standalone-agent              # standalone agent (resolves to ./agents/standalone-agent.yaml)
///   - unit: other-pkg/shared-unit@1.0.0    # qualified + version (ADR-0037 decision 5)
/// </code>
/// <para>
/// <b>ADR-0037:</b> the package manifest is the container manifest only.
/// Per-artefact YAMLs (<c>./units/&lt;name&gt;.yaml</c>,
/// <c>./agents/&lt;name&gt;.yaml</c>, etc.) are kind-discriminated
/// top-level documents in their own right with their own <c>apiVersion</c>,
/// <c>kind</c>, <c>name</c>, <c>description</c>, <c>readme</c>, and
/// <c>requires</c> blocks. The package's effective requirement set at
/// install time is the union of every contained artefact's
/// <c>requires</c>; the package itself does not declare requirements.
/// </para>
/// <para>
/// <b>Reference grammar:</b> within a manifest, references between artefacts
/// use IaC-style local symbols (the bare-name form above) that are mapped to
/// fresh Guids at install time and never persist. Cross-package references
/// to live entities are written as <c>&lt;pkg&gt;/&lt;name&gt;@&lt;version&gt;</c>
/// (version optional — defaults to the most recently installed version per
/// ADR-0037 decision 5). Path-style references like
/// <c>unit://eng/backend</c> are rejected.
/// </para>
/// </remarks>
public class PackageManifest
{
    /// <summary>API version string (e.g. <c>spring.voyage/v1</c>).</summary>
    [YamlMember(Alias = "apiVersion")]
    public string? ApiVersion { get; set; }

    /// <summary>
    /// Document kind discriminator. Must be the literal string
    /// <c>Package</c> (ADR-0037 decision 2). Old-shape values
    /// (<c>UnitPackage</c> / <c>AgentPackage</c>) are rejected by the
    /// parser with the migration hint surfaced through
    /// <see cref="PackageExceptions"/>.
    /// </summary>
    [YamlMember(Alias = "kind")]
    public string? Kind { get; set; }

    /// <summary>Unique package name (must be a valid identifier).</summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>Human-readable single-line summary of the package.</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>
    /// Optional relative path to a markdown file that UIs render for the
    /// "more about this package" affordance (ADR-0037 decision 2). When
    /// omitted, the catalog scanner looks for a sibling <c>README.md</c>
    /// and uses it implicitly if found.
    /// </summary>
    [YamlMember(Alias = "readme")]
    public string? Readme { get; set; }

    /// <summary>
    /// Package version (ADR-0037 decision 5). Opaque string for v0.1; no
    /// semver parsing yet. Two packages with the same <see cref="Name"/>
    /// and different <see cref="Version"/> values may be installed in the
    /// same tenant simultaneously.
    /// </summary>
    [YamlMember(Alias = "version")]
    public string? Version { get; set; }

    /// <summary>
    /// Top-level artefacts bundled in the package. Each entry is a
    /// single-key map keyed by artefact kind (<c>unit:</c>, <c>agent:</c>,
    /// <c>skill:</c>, or <c>workflow:</c>) whose value is either a
    /// bare/qualified reference string or — for <c>unit:</c> /
    /// <c>agent:</c> only — an inline body. Descendants of umbrella units
    /// (their <c>members:</c> list) are discovered recursively at resolve
    /// time and do not need to appear here.
    /// </summary>
    [YamlMember(Alias = "content")]
    public List<ContentEntry>? Content { get; set; }

    /// <summary>
    /// Captured shape of a legacy <c>metadata:</c> block. Present only so
    /// the parser can surface an actionable error when an old-shape
    /// manifest still declares it (ADR-0037 decision 6 —
    /// <c>LegacyMetadataNesting</c>). The current schema hoists
    /// <c>name</c>, <c>description</c>, and <c>readme</c> to the top
    /// level.
    /// </summary>
    [YamlMember(Alias = "metadata")]
    public LegacyPackageMetadata? Metadata { get; set; }

    /// <summary>
    /// Legacy <c>inputs:</c> block — transitional surface for #1724.
    /// ADR-0037 decision 2 deletes the inputs schema; the post-ADR-0037
    /// parser populates this list as empty so consumers from the
    /// previous schema compile during the staged refactor. The <c>inputs:</c>
    /// YAML key is detected and rejected by
    /// <see cref="PackageManifestParser"/> with the
    /// <c>LegacyInputsField</c> error per ADR-0037 decision 6 — operators
    /// never see this surface populated. Removed entirely in #1726.
    /// </summary>
    [YamlIgnore]
    public List<PackageInputDefinition>? Inputs { get; set; }

    /// <summary>
    /// Legacy package-level <c>connectors:</c> block — transitional
    /// surface for #1724. ADR-0037 decision 2 deletes the package-level
    /// connectors block; the post-ADR-0037 parser populates this list as
    /// empty so consumers from the previous schema compile during the
    /// staged refactor. The <c>connectors:</c> YAML key is detected and
    /// rejected by <see cref="PackageManifestParser"/> with the
    /// <c>LegacyPackageConnectorsField</c> error per ADR-0037 decision 6
    /// — operators never see this surface populated. Removed entirely in
    /// #1726 alongside the install-pipeline rewrite that consumes the
    /// per-artefact <c>requires:</c> union instead.
    /// </summary>
    [YamlIgnore]
    public List<RequiredConnector>? Connectors { get; set; }

    /// <summary>
    /// Raw deserialised <c>inputs:</c> YAML block — populated by the
    /// parser only to detect old-shape manifests and raise the
    /// <c>LegacyInputsField</c> error. Never populated for new-shape
    /// manifests.
    /// </summary>
    [YamlMember(Alias = "inputs")]
    public List<object>? RawInputs { get; set; }

    /// <summary>
    /// Raw deserialised package-level <c>connectors:</c> YAML block —
    /// populated by the parser only to detect old-shape manifests and
    /// raise the <c>LegacyPackageConnectorsField</c> error. Never
    /// populated for new-shape manifests.
    /// </summary>
    [YamlMember(Alias = "connectors")]
    public List<object>? RawConnectors { get; set; }
}

/// <summary>
/// Declares a single scalar input for a package — <em>legacy schema</em>.
/// ADR-0037 decision 2 removes <c>inputs:</c> from <c>package.yaml</c>;
/// connector-binding parameters now live on per-artefact <c>requires:</c>
/// blocks. This class survives only as a transitional compile surface
/// for consumers that have not migrated to the new shape; the parser
/// rejects any <c>inputs:</c> block in YAML with
/// <c>LegacyInputsField</c>. Removed in #1726.
/// </summary>
public class PackageInputDefinition
{
    /// <summary>Input key name.</summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>Scalar type.</summary>
    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    /// <summary>Required flag.</summary>
    [YamlMember(Alias = "required")]
    public bool Required { get; set; }

    /// <summary>Secret flag.</summary>
    [YamlMember(Alias = "secret")]
    public bool Secret { get; set; }

    /// <summary>Optional description.</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>Optional default value.</summary>
    [YamlMember(Alias = "default")]
    public string? Default { get; set; }
}

/// <summary>
/// One entry in a package-level <c>connectors:</c> block —
/// <em>legacy schema</em>. ADR-0037 decision 2 removes the package-level
/// connectors declaration; requirements now live on per-artefact
/// <c>requires:</c> blocks. This class survives only as a transitional
/// compile surface for consumers that have not migrated to the new
/// shape; the parser rejects any package-level <c>connectors:</c> block
/// in YAML with <c>LegacyPackageConnectorsField</c>. Removed in #1726.
/// </summary>
public class RequiredConnector
{
    /// <summary>Connector type slug.</summary>
    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    /// <summary>Required flag.</summary>
    [YamlMember(Alias = "required")]
    public bool Required { get; set; } = true;

    /// <summary>Inheritance scope (raw YAML node).</summary>
    [YamlMember(Alias = "inherit")]
    public object? InheritRaw { get; set; }

    /// <summary>True when inheritance is "all" (default).</summary>
    [YamlIgnore]
    public bool InheritAll { get; set; } = true;

    /// <summary>Explicit inheritance unit list (when not "all").</summary>
    [YamlIgnore]
    public IReadOnlyList<string>? InheritUnits { get; set; }
}

/// <summary>
/// One entry in a unit-level <c>connectors:</c> block — <em>legacy
/// schema</em>. ADR-0037 decision 3 renames the unit-level
/// <c>connectors:</c> block to <c>requires:</c> and replaces the typed
/// shape with <see cref="RequirementEntry"/>. This class survives only
/// as a transitional compile surface for consumers that read the old
/// per-unit connector configuration; the parser rejects any unit-level
/// <c>connectors:</c> block with <c>LegacyUnitConnectorsField</c>.
/// Removed in #1726.
/// </summary>
public class ConnectorManifest
{
    /// <summary>Connector type slug.</summary>
    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    /// <summary>Free-form connector configuration.</summary>
    [YamlMember(Alias = "config")]
    public Dictionary<string, object>? Config { get; set; }

    /// <summary>Inheritance opt-out flag.</summary>
    [YamlMember(Alias = "inherit")]
    public bool Inherit { get; set; } = true;
}

/// <summary>
/// One top-level entry in a <see cref="PackageManifest.Content"/> list.
/// Each entry is a single-key YAML mapping whose key is the artefact kind
/// (<c>unit</c>, <c>agent</c>, <c>skill</c>, <c>workflow</c>) and whose
/// value is either a bare/qualified reference string or — for
/// <c>unit</c> / <c>agent</c> only — an inline artefact body. The parser
/// surfaces the discriminator on <see cref="Kind"/> and the reference /
/// inline body on <see cref="Definition"/>.
/// </summary>
public sealed class ContentEntry
{
    /// <summary>
    /// The artefact kind this entry declares, e.g.
    /// <see cref="ArtefactKind.Unit"/>. The YAML key (<c>unit</c>,
    /// <c>agent</c>, <c>skill</c>, <c>workflow</c>) is reflected onto this
    /// property by the parser; it is never present as a separate scalar
    /// on the YAML side.
    /// </summary>
    public ArtefactKind Kind { get; init; }

    /// <summary>
    /// The reference string or inline body declared for this entry.
    /// Bare reference resolves to <c>./units/&lt;name&gt;.yaml</c> /
    /// <c>./agents/&lt;name&gt;.yaml</c> / <c>./skills/&lt;name&gt;.md</c>
    /// / <c>./workflows/&lt;name&gt;/</c> per the artefact kind. A
    /// qualified reference (<c>pkg/name</c> or <c>pkg/name@version</c>)
    /// resolves via the catalog. Inline bodies are accepted only for
    /// <see cref="ArtefactKind.Unit"/> and <see cref="ArtefactKind.Agent"/>.
    /// </summary>
    public InlineArtefactDefinition Definition { get; init; } = default!;
}

/// <summary>
/// Captured shape of the legacy <c>metadata:</c> block. Used only for
/// migration-error reporting per ADR-0037 decision 6.
/// </summary>
public class LegacyPackageMetadata
{
    /// <summary>Captured legacy <c>name</c>.</summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>Captured legacy <c>description</c>.</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>Captured legacy <c>displayName</c>.</summary>
    [YamlMember(Alias = "displayName")]
    public string? DisplayName { get; set; }
}
