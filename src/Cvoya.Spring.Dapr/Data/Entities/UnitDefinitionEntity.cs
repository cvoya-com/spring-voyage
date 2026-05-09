// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using System.Text.Json;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Represents a unit (team) definition stored in the database.
/// A unit groups agents together under a shared orchestration strategy.
/// Identity is the entity Guid <see cref="Id"/> — there is no separate
/// slug column; <see cref="DisplayName"/> is the only human-readable
/// label and is not addressable.
/// </summary>
public class UnitDefinitionEntity : ITenantScopedEntity
{
    /// <summary>Gets or sets the unique identifier for the unit definition (the actor identity).</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the tenant that owns this unit definition.</summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Gets or sets the human-readable display name. NOT unique; not
    /// addressable; renames do not invalidate routing or audit history.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional description of the unit.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the full unit definition stored as JSON.</summary>
    public JsonElement? Definition { get; set; }

    /// <summary>Gets or sets the timestamp when the unit definition was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the timestamp when the unit definition was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Gets or sets the timestamp when the unit definition was soft-deleted, or null if active.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// Structured validation error from the last Validating → Error transition,
    /// as JSON-serialized <see cref="Cvoya.Spring.Core.Units.UnitValidationError"/>.
    /// Null if the most recent probe succeeded or the unit has never been validated.
    /// </summary>
    public string? LastValidationErrorJson { get; set; }

    /// <summary>
    /// Instance id of the Dapr workflow run that last validated this unit, for
    /// debugging and log correlation.
    /// </summary>
    public string? LastValidationRunId { get; set; }

    /// <summary>
    /// Package install lifecycle state (ADR-0035 decision 11).
    /// Rows written by the legacy path default to <see cref="PackageInstallState.Active"/>.
    /// Rows written by <c>IPackageInstallService</c> start at
    /// <see cref="PackageInstallState.Staging"/> and flip to
    /// <see cref="PackageInstallState.Active"/> after successful Phase-2 actor activation.
    /// </summary>
    public PackageInstallState InstallState { get; set; } = PackageInstallState.Active;

    /// <summary>
    /// FK to <see cref="PackageInstallEntity.InstallId"/> for rows created via
    /// <c>IPackageInstallService</c>. <c>null</c> for rows written by the legacy
    /// <c>CreateFromManifestAsync</c> path.
    /// </summary>
    public Guid? InstallId { get; set; }
}
