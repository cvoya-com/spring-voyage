// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

/// <summary>
/// Request body for adding a human as a unit team-role member (#2409,
/// reshaped by ADR-0046 §7). Mirrors the columns on
/// <c>unit_memberships_humans</c>. The POST endpoint is idempotent on the
/// natural key <c>(unit, human)</c> — re-posting the same tuple updates
/// the row's roles / expertise / notifications in place rather than
/// returning 409.
/// </summary>
/// <param name="HumanId">
/// The human's stable Guid identity. Must reference a human row in the
/// current tenant; the server returns 404 when the id is unknown.
/// </param>
/// <param name="Roles">
/// Optional free-form team-role list (e.g. <c>[owner]</c>, <c>[reviewer,
/// security_lead]</c>). ADR-0046 §3 makes this multi-valued; empty list
/// when omitted.
/// </param>
/// <param name="Expertise">
/// Optional list of expertise tags. Empty list when omitted.
/// </param>
/// <param name="Notifications">
/// Optional list of free-form notification event tags. Empty list when
/// omitted.
/// </param>
public sealed record AddUnitHumanMemberRequest(
    [property: Required] System.Guid HumanId,
    IReadOnlyList<string>? Roles = null,
    IReadOnlyList<string>? Expertise = null,
    IReadOnlyList<string>? Notifications = null);

/// <summary>
/// Request body for <c>PATCH /api/v1/tenant/units/{id}/members/humans/{humanId}</c>.
/// Updates the multi-valued fields on an existing membership row
/// (ADR-0046 §5: full replacement on lists). Omitted properties leave the
/// existing list untouched; an explicit empty array clears the field.
/// </summary>
/// <param name="Roles">The new roles list (may be empty); null leaves the existing list intact.</param>
/// <param name="Expertise">The new expertise tag list (may be empty); null leaves the existing list intact.</param>
/// <param name="Notifications">The new notification event tag list (may be empty); null leaves the existing list intact.</param>
public sealed record UpdateUnitHumanMemberRequest(
    IReadOnlyList<string>? Roles = null,
    IReadOnlyList<string>? Expertise = null,
    IReadOnlyList<string>? Notifications = null);

/// <summary>
/// One row of the team-role membership read surface for a unit (#2409,
/// reshaped by ADR-0046 §7). Mirrors the projection emitted by
/// <see cref="Cvoya.Spring.Core.Units.UnitHumanMembership"/> — the
/// synthetic membership Guid is included so the portal / CLI can link
/// directly to the row without re-resolving on the natural key.
/// </summary>
/// <param name="MembershipId">The membership row's synthetic Guid.</param>
/// <param name="HumanId">The human's stable Guid identity.</param>
/// <param name="Roles">The multi-valued team-role list ADR-0046 §3 introduced.</param>
/// <param name="Expertise">The persisted expertise tag list.</param>
/// <param name="Notifications">The persisted notification event tag list.</param>
public sealed record UnitHumanMemberResponse(
    System.Guid MembershipId,
    System.Guid HumanId,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Expertise,
    IReadOnlyList<string> Notifications);
