// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using System.Collections.Generic;

/// <summary>
/// Persists the list of resolved <see cref="SkillBundle"/> instances attached
/// to a unit by its manifest. Prompt-assembly consumers read from here at
/// message-turn time to compose the unit prompt with the bundle prompts and
/// to build the effective tool list the agent can invoke.
/// </summary>
/// <remarks>
/// <para>
/// OSS default implementation is in <c>Cvoya.Spring.Dapr</c> and uses a
/// simple in-memory dictionary backed by the Dapr state store via
/// <see cref="State.IStateStore"/>. The private cloud repo swaps in a
/// tenant-scoped store; call sites depend on this interface so no
/// downstream code has to change.
/// </para>
/// <para>
/// The order of bundles is significant and preserved across round-trips:
/// declaration order in the manifest drives concatenation order in the
/// final prompt, per <c>docs/architecture/packages.md</c>.
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
    /// valid "clear all" operation.
    /// </summary>
    Task SetAsync(
        string unitId,
        IReadOnlyList<SkillBundle> bundles,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes any bundle rows for the unit. Called from unit-delete flows so
    /// orphan rows do not leak. No-op when no rows exist.
    /// </summary>
    Task DeleteAsync(
        string unitId,
        CancellationToken cancellationToken = default);
}