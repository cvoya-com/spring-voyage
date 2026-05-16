// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using System.Collections.Generic;

/// <summary>
/// Validates a set of resolved <see cref="SkillBundle"/> instances against the
/// registered <see cref="ISkillRegistry"/> tool set, the SDK-introspected
/// image-tier surface, and the unit's policy constraints. Called at
/// unit-creation / manifest-apply time so a misconfigured manifest surfaces
/// a clear install-time error rather than failing mid-conversation. Sits
/// between the resolver (which materialises bundles from disk or any other
/// backing store) and the unit creation pipeline.
/// </summary>
public interface ISkillBundleValidator
{
    /// <summary>
    /// Validates every <see cref="SkillBundle.RequiredTools"/> entry against
    /// the configured registries, the image-tier
    /// <see cref="IImageToolsReader"/>, and the unit's skill policy.
    ///
    /// **Strict (#2346)**: an unresolved required tool is blocking — the
    /// validator throws <see cref="SkillBundleValidationException"/> with a
    /// <see cref="SkillBundleValidationProblemReason.ToolNotAvailable"/>
    /// problem and the endpoint layer maps that to a 400 with
    /// <c>code: "RequiredToolUnresolved"</c>. Resolution checks the
    /// declaration's namespace against
    /// <see cref="ISkillRegistry.GetToolsByNamespace"/> first; on miss it
    /// falls back to the image-tier <see cref="IImageToolsReader"/> by
    /// tool name. The lenient "log warning, continue" path from Sub B
    /// (#2335) has been removed.
    ///
    /// Optional requirements (<see cref="SkillToolRequirement.Optional"/>)
    /// remain advisory: a missing optional tool does not block install.
    /// Policy denies are always blocking — the C3 security invariant.
    ///
    /// Returns a <see cref="SkillBundleValidationReport"/> on success;
    /// the report's <see cref="SkillBundleValidationReport.Warnings"/>
    /// list is empty under strict validation but the type is retained
    /// for forward compatibility with future advisory checks.
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
