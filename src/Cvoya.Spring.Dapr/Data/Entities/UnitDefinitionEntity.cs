// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using System.Text.Json;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Represents a unit (team) definition stored in the database.
/// A unit groups agents together under a shared orchestration strategy.
/// </summary>
public class UnitDefinitionEntity : ITenantScopedEntity
{
    /// <summary>Gets or sets the unique identifier for the unit definition.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the tenant that owns this unit definition.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Gets or sets the user-facing identifier for the unit.</summary>
    public string UnitId { get; set; } = string.Empty;

    /// <summary>Gets or sets the Dapr actor identifier used to invoke this unit.</summary>
    public string? ActorId { get; set; }

    /// <summary>Gets or sets the display name of the unit.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional description of the unit.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the full unit definition stored as JSON.</summary>
    public JsonElement? Definition { get; set; }

    /// <summary>Gets or sets the list of member identifiers stored as JSON.</summary>
    public JsonElement? Members { get; set; }

    /// <summary>Gets or sets the timestamp when the unit definition was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the timestamp when the unit definition was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Gets or sets the timestamp when the unit definition was soft-deleted, or null if active.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// Gets or sets a flag indicating whether this unit is a top-level unit —
    /// i.e. its parent is the tenant itself, not another unit. Every unit must
    /// either be top-level OR carry at least one parent-unit edge; the two
    /// cases are mutually exclusive at creation time. Default is <c>false</c>:
    /// a plain (non-top-level) unit must be added as a member of at least one
    /// parent unit.
    /// </summary>
    public bool IsTopLevel { get; set; }

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

}