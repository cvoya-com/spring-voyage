// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Response body for <c>GET /api/v1/tenant/tree</c>. Carries the
/// synthesized tenant-rooted tree consumed by the Explorer surface
/// (<c>&lt;UnitExplorer /&gt;</c> in the portal).
/// </summary>
/// <param name="Tree">Root node (always <c>Kind = "Tenant"</c>).</param>
public record TenantTreeResponse(TenantTreeNode Tree);

/// <summary>
/// One node in the tenant tree. Shape mirrors the Explorer's
/// <c>TreeNode</c> type so the frontend hook can pass the payload through
/// verbatim (subject to the boundary-validation pass described in §3 of
/// the plan and tracked as <c>FOUND-tree-boundary-validate</c>).
/// </summary>
/// <param name="Id">Stable node identifier — slug for units/tenants, address for agents.</param>
/// <param name="Name">Human-readable name rendered in the tree row + detail header.</param>
/// <param name="Kind">
/// One of <c>"Tenant"</c>, <c>"Unit"</c>, <c>"Agent"</c>. The Tenant node
/// is synthesized server-side — it is not persisted anywhere; the
/// Explorer treats it as a navigation root only.
/// </param>
/// <param name="Status">Lifecycle status string (e.g. <c>"running"</c>, <c>"paused"</c>).</param>
/// <param name="Desc">Optional one-line description.</param>
/// <param name="Cost24h">Optional self cost in USD over the last 24 h. Subtree totals are rolled up client-side.</param>
/// <param name="Msgs24h">Optional self message volume over the last 24 h.</param>
/// <param name="Role">Optional agent role (only set on <c>Kind = "Agent"</c> nodes).</param>
/// <param name="Skills">Optional skill count (only set on <c>Kind = "Agent"</c> nodes).</param>
/// <param name="PrimaryParentId">
/// For agent nodes that appear under multiple parent units: the unit id
/// whose membership is flagged <c>IsPrimary</c>. Clients use this to
/// dedupe multi-parent aliases so the agent's "canonical" detail view is
/// opened from one well-known tree position.
/// </param>
/// <param name="Children">Direct children, or <c>null</c> for leaves.</param>
public record TenantTreeNode(
    string Id,
    string Name,
    string Kind,
    string Status,
    string? Desc = null,
    double? Cost24h = null,
    long? Msgs24h = null,
    string? Role = null,
    int? Skills = null,
    string? PrimaryParentId = null,
    IReadOnlyList<TenantTreeNode>? Children = null);
