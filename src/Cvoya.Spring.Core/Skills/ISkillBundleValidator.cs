// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using System.Collections.Generic;

/// <summary>
/// Validates a set of resolved <see cref="SkillBundle"/> instances against the
/// registered <see cref="ISkillRegistry"/> tool set and the unit's policy
/// constraints. Called at unit-creation / manifest-apply time so a misconfigured
/// manifest surfaces a clear error (or, for tolerable issues, a warning) to the
/// caller rather than failing mid-conversation. Sits between the resolver
/// (which materialises bundles from disk or any other backing store) and the
/// unit creation pipeline.
/// </summary>
public interface ISkillBundleValidator
{
    /// <summary>
    /// Validates every <see cref="SkillBundle.RequiredTools"/> entry against
    /// the configured registries and the unit's skill policy.
    ///
    /// Returns a <see cref="SkillBundleValidationReport"/> whose
    /// <see cref="SkillBundleValidationReport.Warnings"/> lists non-blocking
    /// problems — most notably tools that no registered connector surfaces.
    /// Those are advisory: the unit is allowed to be created so the prompt /
    /// bundle authoring workflow isn't blocked by aspirational scaffolding,
    /// and the agent will get a runtime "tool not found" from the LLM tooling
    /// layer if it actually invokes a missing tool.
    ///
    /// Blocking problems (e.g. a tool denied by the unit's
    /// <c>SkillPolicy</c> — the C3 security invariant) still throw
    /// <see cref="SkillBundleValidationException"/> with the consolidated
    /// list of problems.
    /// </summary>
    /// <param name="unitId">
    /// The unit being created / updated (the actor Guid). Passed through to
    /// the policy enforcer so per-unit block lists are honoured.
    /// </param>
    /// <param name="bundles">The resolved bundles referenced by the unit manifest.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<SkillBundleValidationReport> ValidateAsync(
        Guid unitId,
        IReadOnlyList<SkillBundle> bundles,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Outcome of a successful (non-blocking) skill-bundle validation run.
/// Carries advisory <see cref="Warnings"/> about tolerable issues — typically
/// bundles whose declared tools aren't surfaced by any registered connector.
/// Blocking problems are signalled via <see cref="SkillBundleValidationException"/>
/// and never reach this type.
/// </summary>
/// <param name="Warnings">
/// Human-readable warning messages. Always non-null; empty when the bundles
/// resolved cleanly against the registered registries and the unit's policy.
/// Callers typically merge these into the creation response's warnings
/// collection so the wizard / CLI surfaces them alongside manifest-section
/// warnings.
/// </param>
public record SkillBundleValidationReport(IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// Shared empty instance for the common "no warnings" case.
    /// </summary>
    public static SkillBundleValidationReport Empty { get; } =
        new(Array.Empty<string>());
}