// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using System.Text.Json;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Represents a connector definition stored in the database.
/// Connectors bridge external systems (e.g., GitHub, Slack) into the
/// Spring Voyage platform. Identity is the entity Guid <see cref="Id"/>;
/// <see cref="Type"/> is the catalog connector kind slug (e.g.
/// <c>github</c>).
/// </summary>
public class ConnectorDefinitionEntity : ITenantScopedEntity
{
    /// <summary>Gets or sets the unique identifier for the connector definition.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the tenant that owns this connector definition.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Gets or sets the human-readable display name. NOT addressable.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the connector kind — the catalog slug for the
    /// connector type (e.g. <c>github</c>, <c>slack</c>). Catalog slugs
    /// are content-addressable identifiers owned by the package author,
    /// stable across tenants; they intentionally remain strings.
    /// </summary>
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
