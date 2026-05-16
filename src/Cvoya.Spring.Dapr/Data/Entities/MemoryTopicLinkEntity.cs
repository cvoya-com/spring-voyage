// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Junction row linking one <see cref="MemoryEntity"/> to one
/// <see cref="MemoryTopicEntity"/> (#2342). A composite key on
/// <c>(tenant_id, memory_id, topic_id)</c> enforces "at most one link
/// row per pair" without forcing a synthetic <c>Id</c> column. Deleting
/// a topic cascade-removes its link rows; deleting a memory entry
/// likewise removes its links. The underlying memory entries and topics
/// survive deletes from the other side.
/// </summary>
public class MemoryTopicLinkEntity : ITenantScopedEntity
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Foreign key onto <see cref="MemoryEntity.Id"/>.</summary>
    public Guid MemoryId { get; set; }

    /// <summary>Foreign key onto <see cref="MemoryTopicEntity.Id"/>.</summary>
    public Guid TopicId { get; set; }
}
