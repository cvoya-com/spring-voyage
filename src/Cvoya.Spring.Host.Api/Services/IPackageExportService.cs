// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Exports an installed package back to its original YAML manifest
/// (ADR-0035 decisions 9 and 12).
///
/// <para>
/// <b>Round-trip fidelity:</b> the service reads <c>OriginalManifestYaml</c>
/// verbatim from the <c>package_installs</c> row, so comments, key ordering,
/// and all formatting choices made by the operator are preserved.
/// YamlDotNet is never used to re-render the full document.
/// </para>
///
/// <para>
/// <b><c>withValues</c>:</b> when <see langword="true"/> the service splices an
/// <c>inputs:</c> block derived from <c>InputBindings</c> into the returned YAML.
/// Secret-typed inputs are emitted as placeholder references
/// (<c>${{ secrets.&lt;name&gt; }}</c>) rather than cleartext values
/// (ADR-0035 decision 9).
/// </para>
/// </summary>
public interface IPackageExportService
{
    /// <summary>
    /// Exports the package that produced the unit identified by
    /// <paramref name="unitName"/>.
    /// </summary>
    /// <param name="unitName">
    /// The unit's <c>address.path</c> as registered in the directory
    /// (e.g. <c>team/architect</c>).
    /// </param>
    /// <param name="withValues">
    /// When <see langword="true"/>, materialises resolved input values into
    /// the <c>inputs:</c> block of the exported YAML; secrets become
    /// placeholder references.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The export result, or <see langword="null"/> when no install row is
    /// found for the given unit name in the current tenant.
    /// </returns>
    Task<PackageExportResult?> ExportByUnitNameAsync(
        string unitName,
        bool withValues,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports all packages that belong to the install batch identified by
    /// <paramref name="installId"/>.
    /// </summary>
    /// <param name="installId">
    /// The install batch identifier as returned by
    /// <see cref="IPackageInstallService.InstallAsync"/>.
    /// </param>
    /// <param name="withValues">
    /// When <see langword="true"/>, materialises resolved input values into
    /// the <c>inputs:</c> block of the exported YAML; secrets become
    /// placeholder references.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The export result, or <see langword="null"/> when no install row is
    /// found for the given install id in the current tenant.
    /// </returns>
    Task<PackageExportResult?> ExportByInstallIdAsync(
        Guid installId,
        bool withValues,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result returned by <see cref="IPackageExportService"/>.
/// </summary>
/// <param name="PackageName">
/// The package name from <c>metadata.name</c> in the manifest.
/// </param>
/// <param name="Content">
/// The exported YAML as raw bytes (UTF-8 encoded).
/// </param>
/// <param name="ContentType">
/// HTTP content-type to use for the response body.
/// For a single package this is <c>application/x-yaml</c>.
/// </param>
/// <param name="FileName">
/// Suggested <c>Content-Disposition</c> filename (e.g. <c>my-package.yaml</c>).
/// </param>
public sealed record PackageExportResult(
    string PackageName,
    byte[] Content,
    string ContentType,
    string FileName);