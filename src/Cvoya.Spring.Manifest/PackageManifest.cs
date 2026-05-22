// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;

using Cvoya.Spring.Core.Artefacts;
using Cvoya.Spring.Core.Lifecycle;

using YamlDotNet.Serialization;

/// <summary>
/// Discriminates the kind of package post-resolve. Computed by
/// <see cref="PackageManifestParser"/> from the discovered artefact set.
/// </summary>
/// <remarks>
/// A package whose top-level artefacts (under <c>agents/</c>) are all
/// agents (and which has no top-level units under <c>units/</c>) resolves
/// as <see cref="AgentPackage"/>. Every other shape — including the empty
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
/// The package YAML shape under ADR-0043 (decision 2):
/// </para>
/// <code>
/// apiVersion: spring.voyage/v1
/// kind: Package
/// name: my-package
/// description: Single-line summary.
/// readme: README.md            # optional; relative path to a markdown file
/// version: 1.0.0
/// </code>
/// <para>
/// <b>ADR-0043:</b> the package manifest is metadata only. The package's
/// contents are exactly the artefact folders found under the conventional
/// subdirectories (<c>units/</c>, <c>agents/</c>, <c>skills/</c>,
/// <c>workflows/</c>, <c>templates/</c>, <c>connectors/</c>). The legacy
/// <c>content:</c> list from ADR-0037 is removed — the catalog walker
/// discovers artefacts from the filesystem; declaring them a second time
/// would be a duplicate source of truth.
/// </para>
/// <para>
/// <b>ADR-0037:</b> per-artefact <c>package.yaml</c> files are
/// kind-discriminated top-level documents in their own right with their
/// own <c>apiVersion</c>, <c>kind</c>, <c>name</c>, <c>description</c>,
/// <c>readme</c>, and <c>requires</c> blocks. The package's effective
/// requirement set at install time is the union of every contained
/// artefact's <c>requires</c>; the package itself does not declare
/// requirements.
/// </para>
/// <para>
/// <b>Reference grammar:</b> within a manifest, references between artefacts
/// use IaC-style local symbols (bare names) that are mapped to fresh Guids
/// at install time and never persist. Cross-package references to live
/// entities are written as <c>&lt;pkg&gt;/&lt;name&gt;@&lt;version&gt;</c>
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
    /// Optional package-level <c>execution:</c> block (#1679). Member
    /// units inherit these defaults field-wise unless they declare their
    /// own <c>execution:</c> block, in which case the member's non-null
    /// fields win and the package's fields fill the gaps. The
    /// <c>inherit:</c> child key on the package block selects which
    /// member units participate (default: every member; explicit list:
    /// only the named units). Symmetric in shape to the per-unit
    /// <see cref="ExecutionManifest"/> the rest of the platform already
    /// understands — a member unit need only re-declare what it wants
    /// to override.
    /// </summary>
    [YamlMember(Alias = "execution")]
    public PackageExecutionManifest? Execution { get; set; }
}

/// <summary>
/// Package-level <c>execution:</c> block (#1679). Carries the
/// inheritable container <c>image</c> plus an optional
/// <see cref="Inherit"/> selector that constrains which member units
/// pick up the defaults.
/// </summary>
/// <remarks>
/// <para>
/// Under #1679 the package may declare execution defaults at the
/// container level so member units don't have to repeat
/// <c>image: ghcr.io/...:latest</c> across every member YAML. A member
/// unit's own <c>execution:</c> block (when present) is merged
/// field-wise on top of the package defaults — only fields the member
/// declares win. A member that declares no <c>execution:</c> block at
/// all inherits the package's full block. When neither side declares
/// <c>execution.image</c> for an inheriting member, the install
/// pipeline raises <see cref="ExecutionConfigurationsMissingException"/>
/// before any DB writes.
/// </para>
/// <para>
/// The <see cref="Inherit"/> child key follows the v0.1 inheritance
/// shape: omitted (default) means every member inherits; an explicit
/// list of unit names restricts inheritance to those members. There is
/// no opt-out at the unit level — a member that wants different
/// defaults declares its own <c>execution:</c> block; a member that
/// wants no defaults at all is excluded from the
/// <see cref="Inherit"/> list.
/// </para>
/// </remarks>
public class PackageExecutionManifest
{
    /// <summary>Container image reference inherited by member units.</summary>
    [YamlMember(Alias = "image")]
    public string? Image { get; set; }

    /// <summary>
    /// Inheritance selector: omitted (or the literal scalar
    /// <c>all</c>) means every member unit inherits; a YAML sequence of
    /// unit names restricts inheritance to those members. Other shapes
    /// raise a <see cref="PackageParseException"/> at parse time.
    /// </summary>
    [YamlMember(Alias = "inherit")]
    public object? Inherit { get; set; }

    /// <summary>True when every inheritable field is null / whitespace.</summary>
    [YamlIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Image);
}

