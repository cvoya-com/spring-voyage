// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Surfaces the on-disk <c>packages/*</c> tree as a catalog of packages
/// and their contents (unit templates, agent templates, skills, connector
/// and workflow assets). A pluggable interface so the private cloud repo
/// can back the catalog with a tenant-scoped store without altering the
/// portal or CLI contracts.
/// </summary>
public interface IPackageCatalogService
{
    /// <summary>
    /// Lists every package currently reachable from the configured
    /// packages root with per-package summary counts for each content
    /// type. Returns an empty list when the packages directory does not
    /// exist (e.g. the API is running outside the repo).
    /// </summary>
    Task<IReadOnlyList<PackageSummary>> ListPackagesAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the full content lists for a package by name, or
    /// <c>null</c> when the package is not found. The detail view is
    /// what the portal's <c>/packages/[name]</c> route and the CLI's
    /// <c>spring package show</c> verb both render.
    /// </summary>
    Task<PackageDetail?> GetPackageAsync(
        string name,
        CancellationToken cancellationToken);

    /// <summary>
    /// Lists every unit template currently reachable from the configured
    /// packages root (across all packages). This is the wizard-side view
    /// kept from the original #316 iteration — package-aware callers now
    /// prefer <see cref="ListPackagesAsync"/> + <see cref="GetPackageAsync"/>.
    /// </summary>
    Task<IReadOnlyList<UnitTemplateSummary>> ListUnitTemplatesAsync(
        CancellationToken cancellationToken);

    /// <summary>
    /// Loads the raw YAML for the unit template identified by
    /// <paramref name="package"/> and <paramref name="name"/>, or returns
    /// <c>null</c> when the template is not found.
    /// </summary>
    Task<string?> LoadUnitTemplateYamlAsync(
        string package,
        string name,
        CancellationToken cancellationToken);

    /// <summary>
    /// Loads the raw <c>package.yaml</c> text for the named package, or
    /// returns <c>null</c> when the package does not exist in the catalog.
    /// Used by the cross-package resolver during manifest parsing to locate
    /// artefacts declared in other packages (ADR-0035 decision 14).
    /// </summary>
    Task<string?> LoadPackageManifestYamlAsync(
        string packageName,
        CancellationToken cancellationToken);

    /// <summary>
    /// Loads the raw YAML for a single within-package artefact by its kind
    /// and name. Returns <c>null</c> when the file is not found. Used by
    /// the cross-package resolver to resolve bare artefact names from
    /// another package's directory layout.
    /// </summary>
    Task<string?> LoadArtefactYamlAsync(
        string packageName,
        Cvoya.Spring.Manifest.ArtefactKind kind,
        string artefactName,
        CancellationToken cancellationToken);
}