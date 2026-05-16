// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using System.Collections.Generic;

/// <summary>
/// Persists the list of resolved <see cref="SkillBundle"/> instances attached
/// to a unit by its manifest or operator-equip flow. Prompt-assembly
/// consumers read from here at message-turn time to compose the unit prompt
/// (Layer 2) with the bundle prompts and to build the effective tool list
/// the unit can invoke.
/// </summary>
/// <remarks>
/// <para>
/// OSS default implementation is in <c>Cvoya.Spring.Dapr</c> and persists
/// the bundle list as a single JSON document per unit under the shared
/// <see cref="State.IStateStore"/>. The private cloud repo swaps in a
/// tenant-scoped store; call sites depend on this interface so no
/// downstream code has to change.
/// </para>
/// <para>
/// Every mutation re-resolves the supplied
/// <see cref="SkillBundleReference"/> coordinates through
/// <see cref="ISkillBundleResolver"/> before persisting, so the on-disk
/// record always carries the latest prompt + required-tools snapshot. The
/// order of bundles is significant and preserved across round-trips:
/// declaration order in the manifest drives concatenation order in the
/// final prompt, per <c>docs/architecture/packages.md</c>. Operator-equip
/// mutations preserve insertion order so a freshly equipped bundle appears
/// at the end of the list — matching the manifest "declaration order"
/// rule for the operator-driven equip path.
/// </para>
/// </remarks>
public interface IUnitSkillBundleStore
{
    /// <summary>
    /// Returns the bundles attached to the unit, or an empty list when none
    /// have been set.
    /// </summary>
    Task<IReadOnlyList<SkillBundle>> GetAsync(
        string unitId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the bundles attached to the unit. Passing an empty list is a
    /// valid "clear all" operation. Each reference is resolved through
    /// <see cref="ISkillBundleResolver"/> so the persisted record carries
    /// the full prompt + required-tools snapshot.
    /// </summary>
    Task<IReadOnlyList<SkillBundle>> SetAsync(
        string unitId,
        IReadOnlyList<SkillBundleReference> references,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a bundle to the unit's equipped list. Idempotent on the
    /// <c>(packageName, skillName)</c> pair — adding a bundle that is
    /// already equipped re-resolves the entry in place (refreshing its
    /// prompt + required-tools snapshot) without moving it within the
    /// ordered list.
    /// </summary>
    Task<IReadOnlyList<SkillBundle>> AddAsync(
        string unitId,
        SkillBundleReference reference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the bundle identified by <paramref name="packageName"/> +
    /// <paramref name="skillName"/> from the unit's equipped list. No-op
    /// when the bundle is not currently equipped.
    /// </summary>
    Task<IReadOnlyList<SkillBundle>> RemoveAsync(
        string unitId,
        string packageName,
        string skillName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes any bundle rows for the unit. Called from unit-delete flows so
    /// orphan rows do not leak. No-op when no rows exist.
    /// </summary>
    Task DeleteAsync(
        string unitId,
        CancellationToken cancellationToken = default);
}
