// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// Read-side envelope for a human user in the platform (#2266 / #2267).
/// </summary>
/// <remarks>
/// <para>
/// Surface for the Explorer Human page (#2267) and the v0.1 dogfooding
/// flows that need to render a human's display name / email / platform
/// role without joining the directory or paging the auth/me endpoint.
/// The shape mirrors <see cref="Cvoya.Spring.Dapr.Data.Entities.HumanEntity"/>
/// minus the storage-only fields (TenantId, NotificationPreferences) so
/// the read-side wire surface stays minimal until v0.2 introduces
/// notification preferences editing on the Human × Config tab.
/// </para>
/// <para>
/// The membership / connector-identity lists are intentionally not
/// embedded here — those live on dedicated sub-resources
/// (<c>/api/v1/tenant/units/{id}/members/humans</c> for memberships,
/// <c>/api/v1/tenant/humans/{id}/identities</c> for identities) so the
/// per-human GET stays cheap and the lists can be paged independently.
/// </para>
/// </remarks>
/// <param name="Id">Stable UUID primary key.</param>
/// <param name="Username">JWT subject claim. Unique within the tenant.</param>
/// <param name="DisplayName">Human-readable display name. Always non-empty
/// per <see cref="Cvoya.Spring.Dapr.Data.Entities.HumanEntity"/>'s defaulting
/// rule (falls back to username when not explicitly set).</param>
/// <param name="Description">Optional editable description (ADR-0046 §7).
/// Surfaces on the portal's Human × Config tab; null when unset.</param>
/// <param name="Email">Optional e-mail address; null when unset.</param>
/// <param name="PlatformRole">The human's global permission level —
/// <c>Viewer</c> / <c>Operator</c> / <c>Owner</c>. Per ADR-0044 § 1 this
/// is the platform-authority axis; team-role membership lives on the
/// per-unit team-membership rows.</param>
/// <param name="CreatedAt">UTC timestamp when the row was created.</param>
public sealed record HumanResponse(
    Guid Id,
    string Username,
    string DisplayName,
    string? Description,
    string? Email,
    string PlatformRole,
    DateTimeOffset CreatedAt);

/// <summary>
/// Request body for
/// <c>PATCH /api/v1/tenant/humans/{id}</c> (ADR-0046 §7). Updates the
/// human's editable identity fields. Omitted fields are treated as
/// "leave unchanged"; explicit empty strings clear them where the underlying
/// column is nullable.
/// </summary>
/// <param name="DisplayName">
/// New display name when present; <see langword="null"/> leaves the
/// existing value untouched. The handler validates via
/// <c>DisplayNameProblems.ValidateOrProblem</c>.
/// </param>
/// <param name="Description">
/// New description when present; <see langword="null"/> leaves the
/// existing value untouched. Pass an explicit empty string to clear.
/// </param>
public sealed record UpdateHumanRequest(
    string? DisplayName = null,
    string? Description = null);

/// <summary>
/// Request body for <c>POST /api/v1/tenant/humans</c> — mint a new
/// <see cref="Cvoya.Spring.Dapr.Data.Entities.HumanEntity"/> row (a "Hat")
/// outside of the package-install path. Backs <c>spring unit members
/// humans add --display-name &lt;...&gt; --as &lt;tenant-user-ref&gt;</c>
/// (ADR-0062 § 6) and the portal's "create Hat" affordance.
/// </summary>
/// <param name="DisplayName">
/// The new Hat's display name. Required. Validated via
/// <c>DisplayNameProblems.ValidateOrProblem</c>.
/// </param>
/// <param name="Description">
/// Optional single-line description (ADR-0046 §7). Null when unset.
/// </param>
/// <param name="TenantUserId">
/// Explicit <c>TenantUser</c> binding for the new Hat (ADR-0062 § 1).
/// When omitted the server resolves via
/// <see cref="Cvoya.Spring.Core.Tenancy.ITenantUserDefaultResolver"/> —
/// OSS returns <c>OssTenantUserIds.Operator</c>; cloud returns the
/// calling principal. When supplied, validated to reference an existing
/// <c>TenantUser</c> row in the current tenant; an unknown id returns
/// 400.
/// </param>
public sealed record CreateHumanRequest(
    [property: Required] string DisplayName,
    string? Description = null,
    Guid? TenantUserId = null);
