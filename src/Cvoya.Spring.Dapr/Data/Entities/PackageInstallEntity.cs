// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Tracks a single package within an install operation (ADR-0035 decision 11).
/// One row per (install_id, package_name): a multi-package batch produces
/// multiple rows sharing the same <see cref="InstallId"/>.
/// </summary>
public class PackageInstallEntity : ITenantScopedEntity
{
    /// <summary>Row primary key (auto-generated per package in the batch).</summary>
    public Guid Id { get; set; }

    /// <summary>
    /// The shared install batch identifier. All rows in the same
    /// <c>spring package install</c> invocation share this id. Callers
    /// use it for <c>status</c>, <c>retry</c>, and <c>abort</c> operations.
    /// </summary>
    public Guid InstallId { get; set; }

    /// <summary>Tenant that owns this install record.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Package name from <c>metadata.name</c> in the package manifest.</summary>
    public string PackageName { get; set; } = string.Empty;

    /// <summary>Current install status for this package.</summary>
    public PackageInstallStatus Status { get; set; }

    /// <summary>
    /// The original package YAML supplied by the operator, preserved verbatim
    /// (comments, ordering, formatting) so <c>spring package export</c> can
    /// round-trip it exactly (ADR-0035 decision 12).
    /// </summary>
    public string OriginalManifestYaml { get; set; } = string.Empty;

    /// <summary>
    /// JSON-serialised resolved input values for this package. Secret inputs
    /// are stored as secret references (e.g. <c>secret://tenant/key</c>),
    /// never as cleartext values.
    /// </summary>
    public string InputsJson { get; set; } = string.Empty;

    /// <summary>
    /// The local filesystem root from which the package was installed. Stored
    /// so <c>retry</c> can re-resolve artefact files from the same location.
    /// May be null for packages installed from a catalog without a local root.
    /// </summary>
    public string? PackageRoot { get; set; }

    /// <summary>When Phase 1 committed the staging rows.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When Phase 2 completed (success or failure). Null while in progress.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Human-readable error summary for <c>Failed</c> rows. Null unless
    /// <see cref="Status"/> is <see cref="PackageInstallStatus.Failed"/>.
    /// </summary>
    public string? ErrorMessage { get; set; }
}

/// <summary>
/// Lifecycle state for individual artefact rows (<c>unit_definitions</c>,
/// <c>connector_definitions</c>, <c>tenant_skill_bundle_bindings</c>) written
/// by <c>IPackageInstallService</c> (ADR-0035 decision 11).
/// </summary>
public enum PackageInstallState
{
    /// <summary>
    /// Phase 1 committed; Phase-2 actor activation has not yet completed.
    /// </summary>
    Staging = 0,

    /// <summary>Actor activated successfully in Phase 2.</summary>
    Active = 1,

    /// <summary>Actor activation failed in Phase 2.</summary>
    Failed = 2,
}

/// <summary>
/// Lifecycle status for a package in an install batch (ADR-0035 decision 11).
/// </summary>
public enum PackageInstallStatus
{
    /// <summary>
    /// Phase 1 committed; Phase 2 actor activation has not yet completed.
    /// Rows are visible but not yet active.
    /// </summary>
    Staging = 0,

    /// <summary>All actors for this package were successfully activated.</summary>
    Active = 1,

    /// <summary>
    /// At least one actor activation in Phase 2 failed. Staging rows remain
    /// visible; the operator can <c>retry</c> or <c>abort</c>.
    /// </summary>
    Failed = 2,
}