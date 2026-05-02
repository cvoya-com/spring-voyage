// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using System.Text.Json;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Represents a connector definition stored in the database.
/// Connectors bridge external systems (e.g., GitHub, Slack) into the Spring Voyage platform.
/// </summary>
public class ConnectorDefinitionEntity : ITenantScopedEntity
{
    /// <summary>Gets or sets the unique identifier for the connector definition.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the tenant that owns this connector definition.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>Gets or sets the user-facing identifier for the connector.</summary>
    public string ConnectorId { get; set; } = string.Empty;

    /// <summary>Gets or sets the type of the connector (e.g., "github", "slack").</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Gets or sets the connector configuration stored as JSON.</summary>
    public JsonElement? Config { get; set; }

    /// <summary>Gets or sets the timestamp when the connector definition was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the timestamp when the connector definition was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Gets or sets the timestamp when the connector definition was soft-deleted, or null if active.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

    /// <summary>
    /// Package install lifecycle state (ADR-0035 decision 11).
    /// Rows written by the legacy path default to <see cref="PackageInstallState.Active"/>.
    /// </summary>
    public PackageInstallState InstallState { get; set; } = PackageInstallState.Active;

    /// <summary>
    /// FK to <see cref="PackageInstallEntity.InstallId"/>. <c>null</c> for rows
    /// written by the legacy path.
    /// </summary>
    public Guid? InstallId { get; set; }
}