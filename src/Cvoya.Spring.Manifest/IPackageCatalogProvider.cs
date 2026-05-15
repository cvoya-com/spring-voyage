// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// One nested artefact discovered under a named parent artefact in a
/// cross-package catalog walk. Used by <see cref="TemplateResolver"/> to
/// stamp out the nested children of a cross-package template
/// (ADR-0043 §5h archetype-library case).
/// </summary>
/// <param name="Kind">The artefact's kind (Unit / Agent only — nested templates do not activate).</param>
/// <param name="Name">The artefact's <c>name:</c> field (unique within the source package per ADR-0043 §3).</param>
/// <param name="Yaml">Raw YAML body of the artefact's inner <c>package.yaml</c>.</param>
/// <param name="ContainingArtefactName">
/// Immediate containing artefact's name (the artefact whose folder this
/// one lives directly inside), or <c>null</c> when the artefact sits
/// directly under the parent template's folder.
/// </param>
public sealed record NestedArtefactDescriptor(
    ArtefactKind Kind,
    string Name,
    string Yaml,
    string? ContainingArtefactName);

/// <summary>
/// Provides access to the package catalog for cross-package reference
/// resolution during manifest parsing (ADR-0035 decisions 3 and 14).
/// </summary>
/// <remarks>
/// This interface lives in <c>Cvoya.Spring.Manifest</c> rather than
/// <c>Cvoya.Spring.Host.Api</c> so the parser layer is independent of the
/// API host. <c>FileSystemPackageCatalogService</c> (in the API project)
/// implements this interface alongside <c>IPackageCatalogService</c>, giving
/// the API host a single catalog implementation that satisfies both contracts.
/// The private cloud repo can supply its own implementation via DI without
/// touching the manifest or parser code.
/// </remarks>
public interface IPackageCatalogProvider
{
    /// <summary>
    /// Checks whether a package exists in the catalog without loading its content.
    /// Used to produce actionable errors ("package not found" vs "artefact not found")
    /// for cross-package references.
    /// </summary>
    Task<bool> PackageExistsAsync(string packageName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads the raw YAML content of a single artefact from a named package,
    /// or returns <c>null</c> when the artefact does not exist. The caller
    /// already knows the <paramref name="kind"/> so the implementation can
    /// derive the sub-directory without needing additional context.
    /// </summary>
    Task<string?> LoadArtefactYamlAsync(
        string packageName,
        ArtefactKind kind,
        string artefactName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Enumerates the concrete nested children of <paramref name="parentArtefactName"/>
    /// in <paramref name="packageName"/> — every <c>kind: Unit</c> /
    /// <c>kind: Agent</c> artefact discovered anywhere under the parent's
    /// folder tree (its own <c>agents/</c> and <c>units/</c> subdirectories
    /// at any depth). Used by <see cref="TemplateResolver"/> to stamp out
    /// nested children of a cross-package template (ADR-0043 §5h
    /// archetype-library case).
    /// </summary>
    /// <remarks>
    /// The default implementation returns an empty list — implementations
    /// that don't surface the cross-package folder tree (in-flight overlay,
    /// remote registry stubs) opt in by overriding this method. Templates
    /// themselves are <em>not</em> returned; only concrete artefacts that
    /// would activate when the parent is stamped.
    /// </remarks>
    Task<IReadOnlyList<NestedArtefactDescriptor>> EnumerateNestedArtefactsAsync(
        string packageName,
        ArtefactKind parentKind,
        string parentArtefactName,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<NestedArtefactDescriptor>>(Array.Empty<NestedArtefactDescriptor>());
}
