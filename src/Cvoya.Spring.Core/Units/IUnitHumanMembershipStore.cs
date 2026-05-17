// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Read seam over the new <c>unit_memberships_humans</c> table introduced
/// in ADR-0044. Surfaces the unit's team-role membership rows (one per
/// <c>(human, role)</c> triple) for callers that need the domain
/// participation view — primarily the <c>sv.list_members</c> MCP tool and
/// any future "who is the security lead on my team?" routing surface.
/// </summary>
/// <remarks>
/// <para>
/// Defined in <c>Cvoya.Spring.Core</c> so the cloud overlay can register a
/// tenant-aware decorator (audit logging, caching) ahead of the OSS default
/// without taking a dependency on <c>Cvoya.Spring.Dapr</c>. Production DI
/// registers the default via <c>TryAddSingleton</c> per the AGENTS.md
/// extensibility rules so the cloud registration takes precedence.
/// </para>
/// <para>
/// This is a read-only seam in v0.1; writes happen through the install
/// activator. Future surfaces (edit-membership CLI / API) will add their
/// own write seam — keeping reads and writes separated mirrors the
/// existing <see cref="IUnitMemberGraphStore"/> layering.
/// </para>
/// </remarks>
public interface IUnitHumanMembershipStore
{
    /// <summary>
    /// Returns every <c>unit_memberships_humans</c> row for the given unit
    /// in stable order (by <c>created_at</c>, then by membership Guid for
    /// equal timestamps). Each entry carries the membership row's
    /// synthetic Guid (addressable for future edit surfaces), the human's
    /// Guid, the team role, and the row's <c>expertise</c> / <c>notifications</c>
    /// jsonb projections.
    /// </summary>
    /// <param name="unitId">The unit's stable Guid identity.</param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    Task<IReadOnlyList<UnitHumanMembership>> ListByUnitAsync(
        Guid unitId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// One row of <c>unit_memberships_humans</c>, projected onto a frame the
/// MCP surface and any future team-membership read API can consume
/// directly without taking an EF dependency.
/// </summary>
/// <param name="MembershipId">
/// Synthetic membership Guid (the row's primary key). Stable across
/// reads; future "edit this membership" surfaces take this Guid.
/// </param>
/// <param name="HumanId">
/// The human's stable Guid identity. Multiple membership rows may share
/// the same <see cref="HumanId"/> when one human fills multiple roles on
/// the same unit (e.g. OSS operator filling every declared team role).
/// </param>
/// <param name="Role">
/// The team role string from the manifest (free-form in v0.1). Never null
/// or whitespace.
/// </param>
/// <param name="Expertise">
/// The membership row's <c>expertise</c> tags. Empty list when the
/// manifest omitted the field.
/// </param>
/// <param name="Notifications">
/// The membership row's <c>notifications</c> tags. Empty list when the
/// manifest omitted the field.
/// </param>
public sealed record UnitHumanMembership(
    Guid MembershipId,
    Guid HumanId,
    string Role,
    IReadOnlyList<string> Expertise,
    IReadOnlyList<string> Notifications);
