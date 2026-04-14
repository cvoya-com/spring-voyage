// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using System.Collections.Generic;

using Cvoya.Spring.Core;

/// <summary>
/// Thrown by <see cref="ISkillBundleValidator"/> when one or more bundle tool
/// requirements cannot be satisfied at unit-creation time — either because no
/// registered <see cref="ISkillRegistry"/> surfaces the tool, or because the
/// unit's <c>SkillPolicy</c> denies it. Carries a structured
/// <see cref="Problems"/> list so the endpoint layer can render a precise
/// ProblemDetails response without parsing the message.
/// </summary>
public class SkillBundleValidationException : SpringException
{
    /// <summary>
    /// Creates a new <see cref="SkillBundleValidationException"/>.
    /// </summary>
    public SkillBundleValidationException(IReadOnlyList<SkillBundleValidationProblem> problems)
        : base(BuildMessage(problems))
    {
        Problems = problems;
    }

    /// <summary>
    /// The individual validation problems, one per failing tool requirement.
    /// Always non-empty when this exception is thrown.
    /// </summary>
    public IReadOnlyList<SkillBundleValidationProblem> Problems { get; }

    private static string BuildMessage(IReadOnlyList<SkillBundleValidationProblem> problems)
    {
        if (problems.Count == 1)
        {
            return $"Skill bundle validation failed: {problems[0]}";
        }
        return $"Skill bundle validation failed ({problems.Count} problems): "
            + string.Join("; ", problems);
    }
}

/// <summary>
/// A single validation problem: which bundle it came from, which tool, and
/// why validation rejected it.
/// </summary>
/// <param name="PackageName">The bundle's package name.</param>
/// <param name="SkillName">The bundle's skill name.</param>
/// <param name="ToolName">The tool requirement that failed.</param>
/// <param name="Reason">Classification of the failure.</param>
/// <param name="DenyingUnitId">
/// When <see cref="Reason"/> is <see cref="SkillBundleValidationProblemReason.BlockedByUnitPolicy"/>,
/// the id of the unit whose policy denied the tool. <c>null</c> otherwise.
/// </param>
public record SkillBundleValidationProblem(
    string PackageName,
    string SkillName,
    string ToolName,
    SkillBundleValidationProblemReason Reason,
    string? DenyingUnitId = null)
{
    /// <inheritdoc />
    public override string ToString() => Reason switch
    {
        SkillBundleValidationProblemReason.ToolNotAvailable =>
            $"bundle '{PackageName}/{SkillName}' requires tool '{ToolName}', which is not surfaced by any registered connector.",
        SkillBundleValidationProblemReason.BlockedByUnitPolicy =>
            $"bundle '{PackageName}/{SkillName}' requires tool '{ToolName}', which unit '{DenyingUnitId}' blocks via its SkillPolicy.",
        _ => $"bundle '{PackageName}/{SkillName}' tool '{ToolName}' failed validation.",
    };
}

/// <summary>
/// Classifies <see cref="SkillBundleValidationProblem"/> outcomes.
/// </summary>
public enum SkillBundleValidationProblemReason
{
    /// <summary>The tool is not registered by any <see cref="ISkillRegistry"/> in the host.</summary>
    ToolNotAvailable,

    /// <summary>The tool is registered, but a unit <c>SkillPolicy</c> denies it.</summary>
    BlockedByUnitPolicy,
}