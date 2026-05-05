// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;

using YamlDotNet.Serialization;

/// <summary>
/// Discriminates the kind of package post-resolve. Computed by
/// <see cref="PackageManifestParser"/> from the parsed
/// <see cref="PackageManifest.Content"/> entries — there is no
/// <c>kind:</c> field in the YAML schema (#1718 item 1).
/// </summary>
/// <remarks>
/// A package whose <see cref="PackageManifest.Content"/> entries are all
/// agents (and which has no top-level units) resolves as
/// <see cref="AgentPackage"/>. Every other shape — including the empty
/// shape used by upload-mode minimal packages — resolves as
/// <see cref="UnitPackage"/>. The discriminator is purely a downstream
/// convenience for the install-pipeline tests; production code keys off
/// the per-artefact <see cref="ArtefactKind"/> instead.
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
/// <see cref="PackageManifestParser"/>. A package declares its metadata,
/// inputs schema, the artefacts it bundles (<see cref="Content"/>), and any
/// connector dependencies.
/// </summary>
/// <remarks>
/// <para>
/// The package YAML shape (decision 2 in ADR-0035, refined by #1629 PR7
/// and #1718 items 1+2):
/// </para>
/// <code>
/// apiVersion: spring.voyage/v1
/// metadata:
///   name: my-package
///   description: ...
/// inputs:
///   - name: team_name
///     type: string
///     required: true
/// content:
///   - unit: sv-oss-design          # bare = local symbol → ./units/sv-oss-design.yaml
///   - agent: standalone-agent      # standalone agent (resolves to ./agents/standalone-agent.yaml)
///   - unit: other-pkg/shared-unit  # qualified = cross-package
/// </code>
/// <para>
/// <b>#1718 item 1:</b> the historical <c>kind:</c> top-level scalar is
/// gone. The parser infers the package kind from
/// <see cref="Content"/> at resolve time (a package whose top-level
/// content is exclusively agents resolves as
/// <see cref="PackageKind.AgentPackage"/>; everything else is
/// <see cref="PackageKind.UnitPackage"/>). A manifest that still carries
/// <c>kind:</c> is rejected with an actionable message — there are no
/// external v0.1 consumers, so the migration is a clean break.
/// </para>
/// <para>
/// <b>#1718 item 2:</b> the historical flat artefact lists
/// (<c>unit:</c>, <c>agent:</c>, <c>subUnits:</c>, <c>skills:</c>,
/// <c>workflows:</c>) are replaced by a single <see cref="Content"/>
/// list of top-level entries. Descendants — sub-units of an umbrella
/// unit — are discovered recursively from each top-level unit's
/// <c>members:</c> list, removing the <c>subUnits:</c> duplication that
/// previously shadowed the umbrella's own member graph.
/// </para>
/// <para>
/// <b>Reference grammar (#1629 PR7):</b> within a manifest, references
/// between artefacts use IaC-style local symbols (the bare-name form
/// above) that are mapped to fresh Guids at install time and never
/// persist. Cross-package references to live entities are written as
/// 32-char no-dash hex Guids — display-name lookups across packages are
/// gone, since display names aren't unique. Path-style references like
/// <c>unit://eng/backend</c> are rejected.
/// </para>
/// </remarks>
public class PackageManifest
{
    /// <summary>API version string (e.g. <c>spring.voyage/v1</c>).</summary>
    [YamlMember(Alias = "apiVersion")]
    public string? ApiVersion { get; set; }

    /// <summary>
    /// Captured raw <c>kind:</c> value (only present so the parser can
    /// surface an actionable error when an old-shape manifest still
    /// declares it — #1718 item 1). Dropped from the public schema:
    /// every consumer keys off <see cref="PackageKind"/> derived from
    /// <see cref="Content"/>, not this field.
    /// </summary>
    [YamlMember(Alias = "kind")]
    public string? Kind { get; set; }

    /// <summary>Package-level metadata (name, description, etc.).</summary>
    [YamlMember(Alias = "metadata")]
    public PackageMetadata? Metadata { get; set; }

    /// <summary>
    /// Declared inputs for the package. Each entry defines a scalar input
    /// that may be referenced as <c>${{ inputs.&lt;name&gt; }}</c> in any
    /// YAML value within the package.
    /// </summary>
    [YamlMember(Alias = "inputs")]
    public List<PackageInputDefinition>? Inputs { get; set; }

    /// <summary>
    /// Top-level artefacts bundled in the package (#1718 item 2). Each
    /// entry is a single-key map keyed by artefact kind (<c>unit:</c>,
    /// <c>agent:</c>, <c>skill:</c>, or <c>workflow:</c>) whose value is
    /// either a bare/qualified reference string or — for <c>unit:</c> /
    /// <c>agent:</c> only — an inline body. Descendants of umbrella units
    /// (their <c>members:</c> list) are discovered recursively at resolve
    /// time and do not need to appear here.
    /// </summary>
    /// <remarks>
    /// The legacy flat lists (<c>unit:</c>, <c>agent:</c>,
    /// <c>subUnits:</c>, <c>skills:</c>, <c>workflows:</c>) are gone.
    /// Manifests that still declare them are rejected by the parser with
    /// an actionable message pointing at this field.
    /// </remarks>
    [YamlMember(Alias = "content")]
    public List<ContentEntry>? Content { get; set; }

    /// <summary>
    /// Declarative connectors block (#1670). Lists each connector type the
    /// package depends on, whether it is required at install time, and how
    /// its binding inherits to member units. Operators configure each
    /// declared connector once at install time; the resolved binding is
    /// inherited by every member unit unless the unit's own
    /// <c>connectors:</c> block opts out via <c>inherit: false</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Inheritance forms accepted on each entry's <c>inherit</c> slot:
    /// </para>
    /// <list type="bullet">
    ///   <item><description><c>all</c> (default) — every member unit inherits.</description></item>
    ///   <item><description><c>[unit-a, unit-b]</c> — only the named members inherit.</description></item>
    /// </list>
    /// <para>
    /// Per-unit opt-out is expressed in the unit YAML by declaring the
    /// connector slug in the unit's <c>connectors:</c> block with
    /// <c>inherit: false</c>.
    /// </para>
    /// </remarks>
    [YamlMember(Alias = "connectors")]
    public List<RequiredConnector>? Connectors { get; set; }
}

/// <summary>
/// One top-level entry in a <see cref="PackageManifest.Content"/> list
/// (#1718 item 2). Each entry is a single-key YAML mapping whose key is
/// the artefact kind (<c>unit</c>, <c>agent</c>, <c>skill</c>,
/// <c>workflow</c>) and whose value is either a bare/qualified reference
/// string or — for <c>unit</c> / <c>agent</c> only — an inline artefact
/// body. The parser surfaces the discriminator on
/// <see cref="Kind"/> and the reference / inline body on
/// <see cref="Definition"/>.
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
    /// qualified reference (<c>pkg/name</c>) resolves via the catalog.
    /// Inline bodies are accepted only for <see cref="ArtefactKind.Unit"/>
    /// and <see cref="ArtefactKind.Agent"/>; skill / workflow entries
    /// must be a scalar reference.
    /// </summary>
    public InlineArtefactDefinition Definition { get; init; } = default!;
}

/// <summary>
/// One entry in a package's <c>connectors:</c> block (#1670). Declares
/// the connector type the package depends on plus how its binding
/// inherits to member units.
/// </summary>
public class RequiredConnector
{
    /// <summary>
    /// The connector type slug (matches
    /// <c>Cvoya.Spring.Connectors.IConnectorType.Slug</c>) — e.g.
    /// <c>github</c>. The manifest parser validates the slug against the
    /// connector registry at install time; an unknown slug is a parse error.
    /// </summary>
    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    /// <summary>
    /// When <c>true</c>, the install pipeline rejects the request with a
    /// <c>ConnectorBindingMissing</c> 400 if the operator has not supplied
    /// a binding for this connector at install time. Defaults to <c>true</c>
    /// — a connector is declared because it is needed.
    /// </summary>
    [YamlMember(Alias = "required")]
    public bool Required { get; set; } = true;

    /// <summary>
    /// Inheritance scope. Accepts the literal string <c>all</c> (every
    /// member unit inherits — the default) or a YAML sequence of member
    /// unit names (only the named members inherit). The two shapes are
    /// surfaced through <see cref="InheritAll"/> and
    /// <see cref="InheritUnits"/> after parsing — the raw YAML node lives
    /// on <see cref="InheritRaw"/> so the parser can distinguish "absent"
    /// from "explicitly set to all".
    /// </summary>
    /// <remarks>
    /// Per-unit opt-out (<c>inherit: false</c>) is expressed on the unit
    /// side, not here — see <see cref="ConnectorManifest.Inherit"/>.
    /// </remarks>
    [YamlMember(Alias = "inherit")]
    public object? InheritRaw { get; set; }

    /// <summary>
    /// True when <see cref="InheritRaw"/> is absent or the literal string
    /// <c>all</c>. Set by the parser after reading the raw YAML.
    /// </summary>
    [YamlIgnore]
    public bool InheritAll { get; set; } = true;

    /// <summary>
    /// When non-null, the explicit list of member unit names that inherit
    /// the package-level binding. <c>null</c> when <see cref="InheritAll"/>
    /// is <c>true</c>. Set by the parser after reading the raw YAML.
    /// </summary>
    [YamlIgnore]
    public IReadOnlyList<string>? InheritUnits { get; set; }
}

/// <summary>
/// Package-level metadata block.
/// </summary>
public class PackageMetadata
{
    /// <summary>Unique package name (must be a valid identifier).</summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>Human-readable description of the package.</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>Optional display name for the package.</summary>
    [YamlMember(Alias = "displayName")]
    public string? DisplayName { get; set; }
}

/// <summary>
/// Declares a single scalar input for a package. Input types are
/// <c>string</c>, <c>int</c>, <c>bool</c>, and <c>secret</c>
/// (per ADR-0035 decision 8).
/// </summary>
public class PackageInputDefinition
{
    /// <summary>Input key name. Used in <c>${{ inputs.&lt;name&gt; }}</c> expressions.</summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>
    /// Scalar type: <c>string</c> (default), <c>int</c>, or <c>bool</c>.
    /// Use <see cref="Secret"/> for secret-typed inputs.
    /// </summary>
    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    /// <summary>When <c>true</c>, the input must be supplied; an absent value is a parse error.</summary>
    [YamlMember(Alias = "required")]
    public bool Required { get; set; }

    /// <summary>
    /// When <c>true</c>, the input is secret-typed. The value is stored as a
    /// secret reference, not as cleartext. Secret inputs are never round-tripped
    /// in export output as plain values.
    /// </summary>
    [YamlMember(Alias = "secret")]
    public bool Secret { get; set; }

    /// <summary>Human-readable description of the input's purpose.</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>
    /// Optional default value (ignored for <c>required: true</c> inputs when
    /// no value is supplied — a required input with no value is an error even
    /// if a default is declared).
    /// </summary>
    [YamlMember(Alias = "default")]
    public string? Default { get; set; }
}