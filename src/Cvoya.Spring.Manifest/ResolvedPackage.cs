// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;

/// <summary>
/// The fully resolved, validated output of parsing a <c>package.yaml</c>
/// through <see cref="PackageManifestParser"/>. Produced after input
/// substitution, reference resolution, cycle detection, and name-uniqueness
/// validation have all passed.
/// </summary>
public class ResolvedPackage
{
    /// <summary>Package name (from <c>metadata.name</c>).</summary>
    public required string Name { get; init; }

    /// <summary>Optional description (from <c>metadata.description</c>).</summary>
    public string? Description { get; init; }

    /// <summary>Package kind (unit or agent).</summary>
    public required PackageKind Kind { get; init; }

    /// <summary>
    /// The substituted input values, keyed by input name. Secret inputs are
    /// stored as secret references (prefixed with <c>secret://</c>), not as
    /// cleartext values.
    /// </summary>
    public required IReadOnlyDictionary<string, string> InputValues { get; init; }

    /// <summary>Resolved unit artefacts, in declaration order.</summary>
    public required IReadOnlyList<ResolvedArtefact> Units { get; init; }

    /// <summary>Resolved agent artefacts, in declaration order.</summary>
    public required IReadOnlyList<ResolvedArtefact> Agents { get; init; }

    /// <summary>Resolved skill artefacts, in declaration order.</summary>
    public required IReadOnlyList<ResolvedArtefact> Skills { get; init; }

    /// <summary>Resolved workflow artefacts, in declaration order.</summary>
    public required IReadOnlyList<ResolvedArtefact> Workflows { get; init; }

    /// <summary>
    /// Package-level connector declarations (#1670), normalised so the
    /// install pipeline can compute per-unit inheritance without re-walking
    /// the raw YAML. Empty when the package declares no connectors.
    /// </summary>
    public IReadOnlyList<RequiredConnector> Connectors { get; init; } =
        System.Array.Empty<RequiredConnector>();

    /// <summary>
    /// Non-fatal warnings produced during parse / resolve. Used today by the
    /// connector-block validator to surface "unit's connectors entry is
    /// identical to the inherited one" so an operator notices a redundant
    /// override without failing the install.
    /// </summary>
    public IReadOnlyList<string> Warnings { get; init; } =
        System.Array.Empty<string>();
}

/// <summary>
/// One resolved artefact in a <see cref="ResolvedPackage"/>. Carries the
/// original reference string and the on-disk path (for within-package
/// artefacts) or the cross-package coordinates.
/// </summary>
public class ResolvedArtefact
{
    /// <summary>The artefact name (the part after <c>/</c> in a qualified reference, or the whole bare name).</summary>
    public required string Name { get; init; }

    /// <summary>The source package name, or <c>null</c> for within-package artefacts.</summary>
    public string? SourcePackage { get; init; }

    /// <summary>The artefact kind (unit, agent, skill, workflow).</summary>
    public required ArtefactKind Kind { get; init; }

    /// <summary>
    /// Absolute path to the resolved file on disk, or <c>null</c> for
    /// cross-package references (which are resolved via the catalog, not
    /// by a local path).
    /// </summary>
    public string? ResolvedPath { get; init; }

    /// <summary>
    /// The post-substitution YAML content of the artefact, or <c>null</c>
    /// for cross-package references resolved via the catalog.
    /// </summary>
    public string? Content { get; init; }

    /// <summary><c>true</c> when this artefact came from another package.</summary>
    public bool IsCrossPackage => SourcePackage is not null;
}