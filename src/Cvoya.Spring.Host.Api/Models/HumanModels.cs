// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

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
    string? Email,
    string PlatformRole,
    DateTimeOffset CreatedAt);
