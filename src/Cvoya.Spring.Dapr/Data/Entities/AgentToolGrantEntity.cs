// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data.Entities;

using Cvoya.Spring.Core.Tenancy;

/// <summary>
/// Persists one (tenant, agent, tool, provenance) grant row (#2335 Sub B).
/// Replaces the pre-#2335 <c>agent_skill_grants</c> table; the rename and
/// reshape are carried by a single EF migration that copies every existing
/// row across with <c>provenance = "explicit"</c>.
/// </summary>
/// <remarks>
/// <para>
/// The shape splits the canonical tool id into two columns —
/// <see cref="Namespace"/> (e.g. <c>github</c>) and
/// <see cref="ToolName"/> (the full <c>github.create_issue</c> id, kept
/// for index-friendly lookup). Storing both lets the grant resolver
/// answer "every <c>github.*</c> grant on this subject" without parsing
/// strings on every read. <see cref="Provenance"/> is part of the
/// composite primary key so a tool can sit on the subject through more
/// than one source (e.g. <c>explicit</c> + <c>connector:github</c>) and
/// the resolver can drop the source-of-record cleanly when the
/// underlying binding goes away.
/// </para>
/// <para>
/// Uniqueness is enforced on
/// <c>(tenant_id, agent_id, tool_name, provenance)</c> via the composite
/// PK. The grant resolver folds the rows back to one effective entry
/// per tool using the precedence order documented on
/// <see cref="Cvoya.Spring.Core.Skills.IToolGrantResolver"/>.
/// </para>
/// </remarks>
public class AgentToolGrantEntity : ITenantScopedEntity
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Stable Guid identity of the agent the grant attaches to.</summary>
    public Guid AgentId { get; set; }

    /// <summary>
    /// Namespace segment — the portion of <see cref="ToolName"/> before
    /// the first <c>.</c> (e.g. <c>github</c>, <c>sv</c>). Indexed so
    /// "grant every tool under this namespace" reads are a single B-tree
    /// scan.
    /// </summary>
    public string Namespace { get; set; } = string.Empty;

    /// <summary>
    /// Canonical tool id, e.g. <c>github.create_issue</c>. Matches
    /// <see cref="Cvoya.Spring.Core.Skills.ToolDefinition.Name"/>.
    /// </summary>
    public string ToolName { get; set; } = string.Empty;

    /// <summary>
    /// Source of the grant. One of:
    /// <see cref="Cvoya.Spring.Core.Skills.ToolProvenance.Platform"/>,
    /// <see cref="Cvoya.Spring.Core.Skills.ToolProvenance.Explicit"/>, or
    /// a prefixed form
    /// (<see cref="Cvoya.Spring.Core.Skills.ToolProvenance.ConnectorPrefix"/>,
    /// <see cref="Cvoya.Spring.Core.Skills.ToolProvenance.ImagePrefix"/>).
    /// Platform rows are not normally persisted (the <c>sv.*</c> tier is
    /// implicit), but the column accepts the value for symmetry.
    /// </summary>
    public string Provenance { get; set; } = string.Empty;

    /// <summary>UTC timestamp when the grant was first created.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
