// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Threading;
using System.Threading.Tasks;

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
}