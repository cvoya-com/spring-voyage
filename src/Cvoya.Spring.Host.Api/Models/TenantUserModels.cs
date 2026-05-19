// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// Read-side envelope for a <c>TenantUser</c> — the authenticated principal
/// of Spring Voyage scoped to one tenant (ADR-0047 §1). Surfaces on the
/// portal's user-identity page (Phase H) and the CLI's read verbs (Phase G).
/// </summary>
/// <remarks>
/// Mirrors <see cref="Cvoya.Spring.Dapr.Data.Entities.TenantUserEntity"/>
/// minus the storage-only <c>TenantId</c> column. <see cref="AuthSubject"/>
/// is the OAuth <c>sub</c> claim that authenticates this principal — null
/// in OSS dev where the operator row is pinned by the deterministic
/// <c>OssTenantUserIds.Operator</c> UUID instead.
/// </remarks>
/// <param name="Id">Stable UUID primary key.</param>
/// <param name="AuthSubject">OAuth subject claim, or <c>null</c> for OSS dev installs.</param>
/// <param name="DisplayName">Human-readable display name; non-empty.</param>
/// <param name="Description">Optional editable description; null when unset.</param>
/// <param name="CreatedAt">UTC timestamp when the row was created.</param>
/// <param name="UpdatedAt">UTC timestamp of the most recent update.</param>
public sealed record TenantUserResponse(
    Guid Id,
    string? AuthSubject,
    string DisplayName,
    string? Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Request body for <c>PATCH /api/v1/tenant/users/{tenantUserId}</c>.
/// Omitted fields are treated as "leave unchanged"; an explicit empty
/// string clears nullable columns.
/// </summary>
/// <param name="DisplayName">
/// New display name when present; <c>null</c> leaves the existing value
/// untouched. Validated via <c>DisplayNameProblems.ValidateOrProblem</c>.
/// </param>
/// <param name="Description">
/// New description when present; <c>null</c> leaves the existing value
/// untouched. Pass an explicit empty string to clear.
/// </param>
public sealed record UpdateTenantUserRequest(
    string? DisplayName = null,
    string? Description = null);

/// <summary>
/// Request body for upserting a <c>TenantUser</c> ↔ connector display-
/// identity mapping (ADR-0047 §2). The URL carries the
/// <c>tenantUserId</c>; the body carries the connector half of the tuple.
/// </summary>
/// <remarks>
/// Display-identity only — no PAT, no installation override, no auth
/// fields. Outbound credentials live on the unit binding row per
/// ADR-0047 §§ 5–6.
/// </remarks>
/// <param name="ConnectorId">
/// The connector slug (e.g. <c>github</c>). Must match an installed
/// connector's <c>IConnectorType.Slug</c>; the server does not enforce
/// the slug-must-be-installed invariant so identities can be staged
/// before a connector lands.
/// </param>
/// <param name="Username">
/// The connector-side login (e.g. GitHub <c>octocat</c>, Slack
/// <c>alice</c>). No leading <c>@</c>.
/// </param>
/// <param name="DisplayHandle">
/// Optional human-friendly rendering (e.g. <c>"Alice Smith (@alice)"</c>).
/// Falls back to <see cref="Username"/> when null.
/// </param>
public sealed record TenantUserConnectorIdentityRequest(
    [property: Required] string ConnectorId,
    [property: Required] string Username,
    string? DisplayHandle);

/// <summary>
/// Response body for the <c>TenantUser</c> ↔ connector display-identity
/// routes (ADR-0047 §2). Carries the resolved tuple plus audit timestamps
/// so the CLI / portal can render the row without a follow-up GET.
/// </summary>
/// <param name="TenantUserId">The stable <c>TenantUser</c> UUID this identity maps to.</param>
/// <param name="ConnectorId">The connector slug.</param>
/// <param name="Username">The connector-native username.</param>
/// <param name="DisplayHandle">The optional display label.</param>
/// <param name="CreatedAt">UTC timestamp when the mapping was first inserted.</param>
/// <param name="UpdatedAt">UTC timestamp of the most recent update.</param>
public sealed record TenantUserConnectorIdentityResponse(
    Guid TenantUserId,
    string ConnectorId,
    string Username,
    string? DisplayHandle,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);
