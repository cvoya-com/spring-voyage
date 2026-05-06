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