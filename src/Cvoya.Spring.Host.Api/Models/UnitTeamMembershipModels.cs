// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

/// <summary>
/// Request body for adding a human as a unit team-role member (#2409).
/// Mirrors the columns on <c>unit_memberships_humans</c> introduced in
/// ADR-0044 § 3. The POST endpoint is idempotent on the natural key
/// <c>(unit, human, role)</c> — re-posting the same tuple updates the
/// <see cref="Expertise"/> + <see cref="Notifications"/> projections in
/// place rather than returning 409.
/// </summary>
/// <param name="HumanId">
/// The human's stable Guid identity. Must reference a human row in the
/// current tenant; the server returns 404 when the id is unknown.
/// </param>
/// <param name="Role">
/// Free-form team role string (e.g. <c>owner</c>, <c>reviewer</c>,
/// <c>security_lead</c>). ADR-0044 explicitly defers vocabulary to v0.2;
/// only the non-empty-string invariant is enforced server-side.
/// </param>
/// <param name="Expertise">
/// Optional list of expertise tags. Empty list when omitted.
/// </param>
/// <param name="Notifications">
/// Optional list of free-form notification event tags. Empty list when
/// omitted.
/// </param>
public sealed record AddUnitHumanMemberRequest(
    [property: Required] Guid HumanId,
    [property: Required] string Role,
    IReadOnlyList<string>? Expertise = null,
    IReadOnlyList<string>? Notifications = null);

/// <summary>
/// Request body for <c>PATCH /api/v1/tenant/units/{id}/members/humans/{humanId}/{role}</c>.
/// Updates the <see cref="Expertise"/> + <see cref="Notifications"/>
/// projections on an existing membership row. Omitted properties are
/// treated as "set to empty list" — the PATCH replaces the whole tag
/// set so the caller stays a one-shot.
/// </summary>
/// <param name="Expertise">The new expertise tag list (may be empty).</param>
/// <param name="Notifications">The new notification event tag list (may be empty).</param>
public sealed record UpdateUnitHumanMemberRequest(
    IReadOnlyList<string>? Expertise = null,
    IReadOnlyList<string>? Notifications = null);

/// <summary>
/// One row of the team-role membership read surface for a unit (#2409).
/// Mirrors the projection emitted by <see cref="Cvoya.Spring.Core.Units.UnitHumanMembership"/>
/// — the synthetic membership Guid is included so the portal / CLI can
/// link directly to the row without re-resolving on the natural key.
/// </summary>
/// <param name="MembershipId">The membership row's synthetic Guid.</param>
/// <param name="HumanId">The human's stable Guid identity.</param>
/// <param name="Role">The team role string the row binds to.</param>
/// <param name="Expertise">The persisted expertise tag list.</param>
/// <param name="Notifications">The persisted notification event tag list.</param>
public sealed record UnitHumanMemberResponse(
    Guid MembershipId,
    Guid HumanId,
    string Role,
    IReadOnlyList<string> Expertise,
    IReadOnlyList<string> Notifications);
