// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using System.Text.Json;

/// <summary>
/// Represents a connector definition stored in the database.
/// Connectors bridge external systems (e.g., GitHub, Slack) into the Spring Voyage platform.
/// </summary>
public class ConnectorDefinitionEntity
{
    /// <summary>Gets or sets the unique identifier for the connector definition.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the tenant that owns this connector definition.</summary>
    public Guid TenantId { get; set; }

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

    /// <summary>Gets or sets the navigation property to the owning tenant.</summary>
    public TenantEntity? Tenant { get; set; }
}
