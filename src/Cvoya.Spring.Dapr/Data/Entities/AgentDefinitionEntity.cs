// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using System.Text.Json;

/// <summary>
/// Represents an agent definition stored in the database.
/// Contains the configuration and metadata for an agent that can be instantiated as a Dapr actor.
/// </summary>
public class AgentDefinitionEntity
{
    /// <summary>Gets or sets the unique identifier for the agent definition.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the user-facing identifier for the agent (e.g., "ada").</summary>
    public string AgentId { get; set; } = string.Empty;

    /// <summary>Gets or sets the display name of the agent.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the role description for the agent.</summary>
    public string? Role { get; set; }

    /// <summary>Gets or sets the full agent definition stored as JSON.</summary>
    public JsonElement? Definition { get; set; }

    /// <summary>Gets or sets the identifier of the user who created this agent definition.</summary>
    public string? CreatedBy { get; set; }

    /// <summary>Gets or sets the timestamp when the agent definition was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the timestamp when the agent definition was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Gets or sets the timestamp when the agent definition was soft-deleted, or null if active.</summary>
    public DateTimeOffset? DeletedAt { get; set; }

}