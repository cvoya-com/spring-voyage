// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using System.Collections.Generic;

/// <summary>
/// Validates a set of resolved <see cref="SkillBundle"/> instances against the
/// registered <see cref="ISkillRegistry"/> tool set and the unit's policy
/// constraints. Called at unit-creation / manifest-apply time so a misconfigured
/// manifest surfaces a clear error to the caller rather than failing mid-
/// conversation. Sits between the resolver (which materialises bundles from
/// disk or any other backing store) and the unit creation pipeline.
/// </summary>
public interface ISkillBundleValidator
{
    /// <summary>
    /// Validates every <see cref="SkillBundle.RequiredTools"/> entry against
    /// the configured registries and the unit's skill policy. Returns silently
    /// on success; throws <see cref="SkillBundleValidationException"/> with a
    /// consolidated list of problems otherwise.
    /// </summary>
    /// <param name="unitId">
    /// The unit being created / updated. Passed through to the policy enforcer
    /// so per-unit block lists are honoured.
    /// </param>
    /// <param name="bundles">The resolved bundles referenced by the unit manifest.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task ValidateAsync(
        string unitId,
        IReadOnlyList<SkillBundle> bundles,
        CancellationToken cancellationToken = default);
}