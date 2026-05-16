// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using System.Collections.Generic;

/// <summary>
/// Persists the list of resolved <see cref="SkillBundle"/> instances
/// equipped on an agent by the operator-equip flow. Mirrors
/// <see cref="IUnitSkillBundleStore"/> for the agent subject: where the
/// unit store feeds Layer 2 (unit context) of the assembled prompt, the
/// agent store feeds Layer 4 (agent instructions) — so a member agent
/// sees its unit's bundles via Layer 2 inheritance and its own bundles
/// via Layer 4 without an explicit inheritance table (see
/// <c>docs/concepts/skills.md</c>).
/// </summary>
/// <remarks>
/// <para>
/// OSS default implementation is in <c>Cvoya.Spring.Dapr</c> and persists
/// the bundle list as a single JSON document per agent under the shared
/// <see cref="State.IStateStore"/>. The private cloud repo can swap in a
/// tenant-scoped store; call sites depend on this interface so no
/// downstream code has to change.
/// </para>
/// <para>
/// Every mutation re-resolves the supplied
/// <see cref="SkillBundleReference"/> coordinates through
/// <see cref="ISkillBundleResolver"/> before persisting, so the on-disk
/// record always carries the latest prompt + required-tools snapshot.
/// The order of bundles is significant and preserved across round-trips
/// — declaration order drives concatenation order in the Layer 4 render.
/// Operator-equip mutations preserve insertion order so a freshly
/// equipped bundle appears at the end of the list.
/// </para>
/// </remarks>
public interface IAgentSkillBundleStore
{
    /// <summary>
    /// Returns the bundles equipped on the agent, or an empty list when
    /// none have been set.
    /// </summary>
    Task<IReadOnlyList<SkillBundle>> GetAsync(
        string agentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the bundles equipped on the agent. Passing an empty list
    /// is a valid "clear all" operation. Each reference is resolved
    /// through <see cref="ISkillBundleResolver"/> so the persisted record
    /// carries the full prompt + required-tools snapshot.
    /// </summary>
    Task<IReadOnlyList<SkillBundle>> SetAsync(
        string agentId,
        IReadOnlyList<SkillBundleReference> references,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a bundle to the agent's equipped list. Idempotent on the
    /// <c>(packageName, skillName)</c> pair — adding a bundle that is
    /// already equipped re-resolves the entry in place (refreshing its
    /// prompt + required-tools snapshot) without moving it within the
    /// ordered list.
    /// </summary>
    Task<IReadOnlyList<SkillBundle>> AddAsync(
        string agentId,
        SkillBundleReference reference,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the bundle identified by <paramref name="packageName"/> +
    /// <paramref name="skillName"/> from the agent's equipped list.
    /// No-op when the bundle is not currently equipped.
    /// </summary>
    Task<IReadOnlyList<SkillBundle>> RemoveAsync(
        string agentId,
        string packageName,
        string skillName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes any bundle rows for the agent. Called from agent-delete
    /// flows so orphan rows do not leak. No-op when no rows exist.
    /// </summary>
    Task DeleteAsync(
        string agentId,
        CancellationToken cancellationToken = default);
}
