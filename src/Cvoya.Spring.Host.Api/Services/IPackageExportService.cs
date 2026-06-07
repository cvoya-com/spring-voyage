// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Exports an installed package as a re-installable <c>package.yaml</c> tree.
///
/// <para>
/// <b>Runtime/DB config is the single source of truth (#3090; supersedes
/// ADR-0035 decision 12).</b> The service <i>reconstructs</i> the package from
/// the live relational stores — definitions, live-config / execution,
/// memberships, expertise, policies, and connector bindings — so every
/// post-deploy edit is reflected. It does not replay the captured
/// <c>original_manifest_yaml</c> blob (that is install-replay provenance only).
/// Comment / key-order fidelity from the original authoring is deliberately
/// traded for export correctness after edits.
/// </para>
///
/// <para>
/// <b><c>withValues</c>:</b> reserved for materialising an <c>inputs:</c> block
/// in a future revision. Reconstruction always emits connector requirements as
/// <c>requires:</c> placeholders and never exports connector config or bound
/// secrets, so no cleartext credential can leak regardless of the flag.
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
