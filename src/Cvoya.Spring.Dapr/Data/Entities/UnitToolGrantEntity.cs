// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persists one (tenant, unit, tool, provenance) grant row (#2335 Sub B).
/// Unit counterpart to <see cref="AgentToolGrantEntity"/>; the auto-grant
/// pipeline writes one row per <c>&lt;ToolNamespace&gt;.*</c> tool on
/// connector bind with <c>provenance = "connector:&lt;Slug&gt;"</c>, and
/// removes them on unbind.
/// </summary>
/// <remarks>
/// Same shape as the agent table — see <see cref="AgentToolGrantEntity"/>
/// for the full contract narrative. Uniqueness is enforced on
/// <c>(tenant_id, unit_id, tool_name, provenance)</c> via the composite
/// PK so a unit can carry the same tool from more than one source
/// (explicit row + connector binding) without conflict.
/// </remarks>
public class UnitToolGrantEntity : ITenantScopedEntity
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Stable Guid identity of the unit the grant attaches to.</summary>
    public Guid UnitId { get; set; }

    /// <summary>Namespace segment (e.g. <c>github</c>, <c>sv</c>).</summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>Canonical tool id, e.g. <c>github.create_issue</c>.</summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>
    /// Source of the grant (see
    /// <see cref="AgentToolGrantEntity.Provenance"/> for the enumeration).
    /// </summary>
    public string Provenance { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the grant was first created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
