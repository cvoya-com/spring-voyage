// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Two-phase package install service (ADR-0035 decision 11).
/// Supports atomic multi-unit installation and atomic multi-package batch
/// installation with topological dep-graph resolution.
/// </summary>
/// <remarks>
/// Phase 1 (single EF transaction): validate all targets, resolve inputs,
/// topo-sort by cross-package references, validate dep-graph closure,
/// pre-flight name collisions, write all rows with <c>state = staging</c>.
/// Any failure rolls back the whole transaction (zero rows survive).
///
/// Phase 2 (post-commit): activate actors in dependency order, flip
/// <c>state = active</c> per row. Activation failures leave staging rows
/// visible for operator-visible recovery via <see cref="RetryAsync"/> /
/// <see cref="AbortAsync"/>.
/// </remarks>
public interface IPackageInstallService
{
    /// <summary>
    /// Installs one or more packages as a single atomic batch.
    /// Returns an <see cref="InstallResult"/> carrying the shared
    /// <c>install_id</c> and per-package outcomes.
    /// </summary>
    Task<InstallResult> InstallAsync(
        IReadOnlyList<InstallTarget> targets,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the current status of an install batch, or <c>null</c> when
    /// no rows exist for the given <paramref name="installId"/> in the current tenant.
    /// </summary>
    Task<InstallStatus?> GetStatusAsync(
        Guid installId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-runs Phase 2 against any rows for <paramref name="installId"/>
    /// whose state is not yet <c>active</c>. Phase 1 stays intact.
    /// Returns the updated <see cref="InstallResult"/> reflecting the retry outcome.
    /// </summary>
    Task<InstallResult> RetryAsync(
        Guid installId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes every row carrying <paramref name="installId"/> across
    /// <c>package_installs</c>, <c>unit_definitions</c>,
    /// <c>connector_definitions</c>, and <c>tenant_skill_bundle_bindings</c>.
    /// Runs in a single EF transaction. After abort the install is gone.
    /// </summary>
    Task AbortAsync(
        Guid installId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One package in an <c>IPackageInstallService.InstallAsync</c> call.
/// </summary>
/// <param name="PackageName">
/// The package name (must match <c>metadata.name</c> in the YAML).
/// </param>
/// <param name="Inputs">
/// Resolved input values for this package, keyed by input name. Secret
/// inputs should already carry the <c>secret://</c> reference form.
/// </param>
/// <param name="OriginalYaml">
/// The raw package YAML to persist verbatim in <c>package_installs</c>.
/// Preserves comments, ordering, and formatting for round-trip fidelity.
/// </param>
/// <param name="PackageRoot">
/// The directory root for resolving within-package artefact references.
/// May be <c>null</c> when the package is catalog-sourced and the catalog
/// provider resolves all references without a local root.
/// </param>
public record InstallTarget(
    string PackageName,
    IReadOnlyDictionary<string, string> Inputs,
    string OriginalYaml,
    string? PackageRoot = null);

/// <summary>
/// Outcome of a single <c>IPackageInstallService.InstallAsync</c> call.
/// </summary>
/// <param name="InstallId">
/// The shared batch identifier. All rows in the batch carry this id.
/// </param>
/// <param name="PackageResults">
/// Per-package outcomes, one entry per package in the install batch.
/// </param>
public record InstallResult(
    Guid InstallId,
    IReadOnlyList<PackageInstallResult> PackageResults);

/// <summary>
/// Per-package outcome within an <see cref="InstallResult"/>.
/// </summary>
/// <param name="PackageName">The package name.</param>
/// <param name="Status">Current install status for this package.</param>
/// <param name="ErrorMessage">Activation error detail if status is Failed.</param>
public record PackageInstallResult(
    string PackageName,
    PackageInstallOutcome Status,
    string? ErrorMessage = null);

/// <summary>
/// Current status snapshot for an install batch, returned by
/// <see cref="IPackageInstallService.GetStatusAsync"/>.
/// </summary>
/// <param name="InstallId">The batch identifier.</param>
/// <param name="Packages">Per-package status entries.</param>
public record InstallStatus(
    Guid InstallId,
    IReadOnlyList<PackageInstallResult> Packages);

/// <summary>
/// Per-package install outcome visible to callers of
/// <see cref="IPackageInstallService"/>.
/// </summary>
public enum PackageInstallOutcome
{
    /// <summary>Phase 1 committed; Phase 2 activation not yet complete.</summary>
    Staging,

    /// <summary>All actors for this package were successfully activated.</summary>
    Active,

    /// <summary>At least one actor activation failed; operator action needed.</summary>
    Failed,
}