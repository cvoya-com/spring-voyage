// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

/// <summary>
/// Represents a tenant in the Spring Voyage platform.
/// Each tenant is an isolated organizational boundary containing agents, units, and connectors.
/// </summary>
public class TenantEntity
{
    /// <summary>Gets or sets the unique identifier for the tenant.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the display name of the tenant.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional description of the tenant.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the timestamp when the tenant was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the timestamp when the tenant was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Gets or sets the timestamp when the tenant was soft-deleted, or null if active.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}
