// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using System.Text.Json;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Represents an agent definition stored in the database.
/// Contains the configuration and metadata for an agent that can be
/// instantiated as a Dapr actor. Identity is the entity Guid
/// <see cref="Id"/> — there is no separate slug column;
/// <see cref="DisplayName"/> is the only human-readable label and is
/// not addressable.
/// </summary>
public class AgentDefinitionEntity : ITenantScopedEntity
{
    /// <summary>Gets or sets the unique identifier for the agent definition (the actor identity).</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the tenant that owns this agent definition.</summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Gets or sets the human-readable display name. NOT unique; not
    /// addressable; renames do not invalidate routing or audit history.
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Gets or sets an optional description of the agent.</summary>
    public string? Description { get; set; }

    /// <summary>Gets or sets the role description for the agent.</summary>
    public string? Role { get; set; }

    /// <summary>Gets or sets the full agent definition stored as JSON.</summary>
    public JsonElement? Definition { get; set; }

    /// <summary>
    /// Gets or sets the serialised array of <c>ToolDefinition</c> records
    /// the agent's running image advertised on <c>GET /a2a/tools</c> at the
    /// most recent deploy / image-rotation (#2336 / Sub C of #2332).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>null</c> until the first introspection call lands. <c>[]</c> when
    /// the agent advertises no image-tier tools or the introspection call
    /// failed (the deploy still succeeds — see
    /// <c>IAgentToolsIntrospector</c>).
    /// </para>
    /// <para>
    /// The grant pipeline (Sub B #2335) reads this column defensively and
    /// surfaces each entry with provenance <c>image:&lt;digest&gt;</c>;
    /// removal of a registration plus a re-deploy clears the column at the
    /// next introspection call so the grant set tracks the live image.
    /// </para>
    /// </remarks>
    public JsonElement? ImageTools { get; set; }

    /// <summary>Gets or sets the FK to <see cref="HumanEntity.Id"/> for the human who created this agent definition.</summary>
    public Guid? CreatedBy { get; set; }

    /// <summary>Gets or sets the timestamp when the agent definition was created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Gets or sets the timestamp when the agent definition was last updated.</summary>
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Gets or sets the timestamp when the agent definition was soft-deleted, or null if active.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}
