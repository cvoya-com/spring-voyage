// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Read / write seam over the <c>unit_memberships_humans</c> table introduced
/// in ADR-0044 and reshaped by ADR-0046 §7. Surfaces the unit's team-role
/// membership rows (one per <c>(unit, human)</c> pair; <c>roles</c> is now a
/// jsonb list on the row) for callers that need the domain participation
/// view — primarily the <c>sv.directory.list_members</c> MCP tool, the operator-facing
/// CLI / REST add-member surface (#2409), and any future "who is the
/// security lead on my team?" routing surface.
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
/// Writes share the set-semantic invariant from ADR-0046 §7: the natural
/// key is <c>(tenant, unit, human)</c> and re-asserting the same key
/// updates <c>roles</c> / <c>expertise</c> / <c>notifications</c> rather
/// than inserting a duplicate row. Tenant scoping is applied by the
/// underlying provider's query filter (the <see cref="UnitHumanMembership"/>
/// projection never exposes the tenant id).
/// </para>
/// </remarks>
public interface IUnitHumanMembershipStore
{
    /// <summary>
    /// Returns every <c>unit_memberships_humans</c> row for the given unit
    /// in stable order (by <c>created_at</c>, then by membership Guid for
    /// equal timestamps). Each entry carries the membership row's
    /// synthetic Guid (addressable for edit surfaces), the human's Guid,
    /// the row's multi-valued team roles list, and the row's
    /// <c>expertise</c> / <c>notifications</c> jsonb projections.
    /// </summary>
    /// <param name="unitId">The unit's stable Guid identity.</param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    Task<IReadOnlyList<UnitHumanMembership>> ListByUnitAsync(
        Guid unitId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a single membership row by the <c>(unit, human)</c> natural
    /// key (ADR-0046 §7). Returns <see langword="null"/> when no row
    /// matches.
    /// </summary>
    /// <param name="unitId">The unit's stable Guid identity.</param>
    /// <param name="humanId">The human's stable Guid identity.</param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    Task<UnitHumanMembership?> GetAsync(
        Guid unitId,
        Guid humanId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Idempotently asserts a membership row for the
    /// <c>(unit, human)</c> natural key (ADR-0046 §7). When a row already
    /// exists the implementation overwrites <c>roles</c> +
    /// <c>expertise</c> + <c>notifications</c> in place; when no row
    /// exists it inserts a fresh one with a new synthetic id and the
    /// current UTC timestamp. The returned record is the post-write
    /// projection of the row.
    /// </summary>
    /// <param name="unitId">The unit's stable Guid identity.</param>
    /// <param name="humanId">The human's stable Guid identity.</param>
    /// <param name="roles">The free-form team-role list (may be empty).</param>
    /// <param name="expertise">The expertise tag list (may be empty).</param>
    /// <param name="notifications">The notification event tag list (may be empty).</param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    Task<UnitHumanMembership> UpsertAsync(
        Guid unitId,
        Guid humanId,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> expertise,
        IReadOnlyList<string> notifications,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the membership row for the <c>(unit, human)</c> natural
    /// key (ADR-0046 §7). Returns <see langword="true"/> when a row was
    /// deleted and <see langword="false"/> when nothing matched — both
    /// outcomes are terminal for the caller (the CLI / REST DELETE
    /// surface treats the "no row" case as a no-op).
    /// </summary>
    /// <param name="unitId">The unit's stable Guid identity.</param>
    /// <param name="humanId">The human's stable Guid identity.</param>
    /// <param name="cancellationToken">Propagates request cancellation.</param>
    Task<bool> RemoveAsync(
        Guid unitId,
        Guid humanId,
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
/// The human's stable Guid identity. ADR-0046 §7 makes this the natural
/// key alongside the unit id — one row per human per unit.
/// </param>
/// <param name="Roles">
/// Free-form team-role strings from the manifest (ADR-0046 §3). Multi-
/// valued; empty list when the manifest omitted the field.
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
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Expertise,
    IReadOnlyList<string> Notifications);
