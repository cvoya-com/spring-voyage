// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persists one memory topic owned by an agent or unit (#2342). Topics
/// group memory entries so the LLM can recall by name; the
/// <c>(tenant_id, owner_scheme, owner_id, name)</c> tuple is unique so
/// an agent and a unit may share a topic name without conflict.
/// </summary>
public class MemoryTopicEntity : ITenantScopedEntity
{
    /// <summary>Stable Guid identifier for the topic.</summary>
    public Guid Id { get; set; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>
    /// Address scheme of the owning addressable — one of
    /// <c>"agent"</c> or <c>"unit"</c>.
    /// </summary>
    public string OwnerScheme { get; set; } = string.Empty;

    /// <summary>Stable Guid identity of the owning addressable.</summary>
    public Guid OwnerId { get; set; }

    /// <summary>Owner-unique human-readable topic name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Optional free-text description.</summary>
    public string? Description { get; set; }

    /// <summary>UTC timestamp when the topic was first created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>UTC timestamp of the last name / description mutation.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
}
