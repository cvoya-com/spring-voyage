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

/// <summary>
/// Request body for <c>PATCH /api/v1/tenant/users/{tenantUserId}/primary-human</c>
/// (ADR-0062 § 2). Pins which of the user's bound <see cref="Cvoya.Spring.Dapr.Data.Entities.HumanEntity"/>
/// rows is the default <c>From</c> for new outbound messages (composer-
/// launched sends, CLI <c>spring message send</c> without <c>--as</c>).
/// </summary>
/// <remarks>
/// The handler validates that <see cref="HumanId"/> names a Human bound
/// to the target tenant user — passing an unbound Human returns 400 with
/// a CLI-friendly message. The column is nullable on the entity but the
/// PATCH always sets a non-empty value; clearing the pin is a follow-up
/// surface (no operator UX needs it today — a fresh row defaults to
/// <c>null</c> and is auto-set on first Human bind).
/// </remarks>
/// <param name="HumanId">
/// The Human (Hat) id to pin as the user's primary sender. Required;
/// must be bound to the target tenant user via <c>humans.tenant_user_id</c>.
/// </param>
public sealed record SetPrimaryHumanRequest(
    [property: Required] Guid HumanId);

/// <summary>
/// Response body for <c>PATCH /api/v1/tenant/users/{tenantUserId}/primary-human</c>.
/// Echoes the post-write pair so the CLI / portal can render the new
/// pin without a follow-up GET.
/// </summary>
/// <param name="TenantUserId">The tenant user whose pin was updated.</param>
/// <param name="PrimaryHumanId">The Hat now pinned as the default sender.</param>
public sealed record SetPrimaryHumanResponse(
    Guid TenantUserId,
    Guid PrimaryHumanId);

/// <summary>
/// One row of the calling caller's bound-Human ("Hat") set
/// (ADR-0062 § 5). Returned by
/// <c>GET /api/v1/tenant/users/me/humans</c> and consumed by the portal's
/// <c>HumanFromSelector</c> + per-Hat inbox rendering. Mirrors the
/// CLI's <c>spring user hats list</c> shape so both surfaces speak the
/// same vocabulary.
/// </summary>
/// <param name="HumanId">Stable UUID of the bound <c>Human</c> row.</param>
/// <param name="DisplayName">
/// The Human row's display name (the "Bob" half of "Bob — designer in
/// Magazine"). Used by the from-selector and by the per-Hat inbox
/// chip.
/// </param>
/// <param name="IsPrimary">
/// True when this Hat is the caller's <c>TenantUser.PrimaryHumanId</c>
/// (ADR-0062 § 2). The portal's new-outbound composer defaults to the
/// primary Hat when the caller has no thread context.
/// </param>
/// <param name="Memberships">
/// The set of unit-memberships this Hat is a member of, with each
/// row's team-role list. Empty when the Human is a tenant-scoped row
/// with no unit attachments (rare; surfaces in OSS dev when a Human is
/// seeded outside a package install).
/// </param>
public sealed record CallerHumanResponse(
    Guid HumanId,
    string DisplayName,
    bool IsPrimary,
    IReadOnlyList<CallerHumanMembershipResponse> Memberships);

/// <summary>
/// One per-unit row of a Hat's membership set
/// (<see cref="CallerHumanResponse.Memberships"/>). Carries the unit
/// the Hat is bound to and the team roles the membership row records,
/// so the portal's from-selector can render the per-Hat context label
/// (e.g. "designer in Magazine") without a second round-trip.
/// </summary>
/// <param name="UnitId">Stable UUID of the unit the Hat is a member of.</param>
/// <param name="UnitDisplayName">
/// The unit's display name (the "Magazine" half of the per-Hat label).
/// </param>
/// <param name="Roles">
/// The membership row's free-form team-role list (e.g. <c>[designer]</c>,
/// <c>[reviewer, security_lead]</c>). Empty when the membership has no
/// roles assigned.
/// </param>
public sealed record CallerHumanMembershipResponse(
    Guid UnitId,
    string UnitDisplayName,
    IReadOnlyList<string> Roles);
