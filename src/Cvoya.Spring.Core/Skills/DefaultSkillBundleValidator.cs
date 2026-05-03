// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using System.Collections.Generic;
using System.Linq;

using Cvoya.Spring.Core.Policies;

/// <summary>
/// Default OSS <see cref="ISkillBundleValidator"/>. Collects all tool names
/// surfaced by the registered <see cref="ISkillRegistry"/> instances (case-
/// insensitive) and classifies problems by severity:
///
/// * <see cref="SkillBundleValidationProblemReason.ToolNotAvailable"/> →
///   non-blocking warning returned in <see cref="SkillBundleValidationReport.Warnings"/>.
///   Skill bundles often declare aspirational unit-orchestration tools that no
///   connector surfaces yet (e.g. the `assignToAgent` / `requestReview`
///   primitives in the shipped `packages/software-engineering/` bundles);
///   rejecting those would block users from creating units from the shipped
///   templates. The agent will get a runtime "tool not found" error from the
///   LLM tooling layer if it actually tries to invoke the missing tool — see
///   #306 for the platform-level follow-up.
/// * <see cref="SkillBundleValidationProblemReason.BlockedByUnitPolicy"/> →
///   blocking, throws <see cref="SkillBundleValidationException"/>. This is
///   the C3 security invariant: a unit's <see cref="SkillPolicy"/> must be
///   honoured at create time just as it is at call time.
///
/// Future problem kinds default to throwing unless they are explicitly
/// categorised as advisory in <see cref="IsWarning"/>.
/// </summary>
public class DefaultSkillBundleValidator : ISkillBundleValidator
{
    private readonly IReadOnlyList<ISkillRegistry> _registries;
    private readonly IUnitPolicyRepository _policyRepository;

    /// <summary>
    /// Creates a new <see cref="DefaultSkillBundleValidator"/>.
    /// </summary>
    public DefaultSkillBundleValidator(
        IEnumerable<ISkillRegistry> registries,
        IUnitPolicyRepository policyRepository)
    {
        _registries = registries.ToList();
        _policyRepository = policyRepository;
    }

    /// <inheritdoc />
    public async Task<SkillBundleValidationReport> ValidateAsync(
        Guid unitId,
        IReadOnlyList<SkillBundle> bundles,
        CancellationToken cancellationToken = default)
    {
        if (bundles.Count == 0)
        {
            return SkillBundleValidationReport.Empty;
        }

        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var registry in _registries)
        {
            foreach (var tool in registry.GetToolDefinitions())
            {
                available.Add(tool.Name);
            }
        }

        var policy = await _policyRepository.GetAsync(unitId, cancellationToken);
        var skillPolicy = policy.Skill;

        var blocking = new List<SkillBundleValidationProblem>();
        var warnings = new List<string>();

        foreach (var bundle in bundles)
        {
            foreach (var requirement in bundle.RequiredTools)
            {
                if (requirement.Optional && !available.Contains(requirement.Name))
                {
                    // Optional tools may be absent; skip missing-tool check but
                    // still apply the policy check so a unit-blocked tool is
                    // still flagged even when the requirement is optional
                    // (blocked-but-advertised is a stronger signal than missing).
                    continue;
                }

                if (!available.Contains(requirement.Name))
                {
                    var problem = new SkillBundleValidationProblem(
                        bundle.PackageName,
                        bundle.SkillName,
                        requirement.Name,
                        SkillBundleValidationProblemReason.ToolNotAvailable);
                    Classify(problem, blocking, warnings);
                    continue;
                }

                if (skillPolicy is not null && IsBlocked(skillPolicy, requirement.Name))
                {
                    var problem = new SkillBundleValidationProblem(
                        bundle.PackageName,
                        bundle.SkillName,
                        requirement.Name,
                        SkillBundleValidationProblemReason.BlockedByUnitPolicy,
                        DenyingUnitId: unitId.ToString());
                    Classify(problem, blocking, warnings);
                }
            }
        }

        if (blocking.Count > 0)
        {
            throw new SkillBundleValidationException(blocking);
        }

        return warnings.Count == 0
            ? SkillBundleValidationReport.Empty
            : new SkillBundleValidationReport(warnings);
    }

    /// <summary>
    /// Routes a problem to the warnings list (advisory, non-blocking) or the
    /// blocking list (will throw). Keeps the per-reason categorisation in a
    /// single place so future problem kinds land in the blocking bucket by
    /// default — a reviewer deciding to demote a reason to a warning has to
    /// touch this method.
    /// </summary>
    private static void Classify(
        SkillBundleValidationProblem problem,
        List<SkillBundleValidationProblem> blocking,
        List<string> warnings)
    {
        if (IsWarning(problem.Reason))
        {
            warnings.Add(FormatWarning(problem));
        }
        else
        {
            blocking.Add(problem);
        }
    }

    /// <summary>
    /// True for reasons that surface as <see cref="SkillBundleValidationReport.Warnings"/>
    /// rather than blocking the creation call. Keep this intentionally
    /// allow-listed — new reasons default to blocking.
    /// </summary>
    private static bool IsWarning(SkillBundleValidationProblemReason reason) =>
        reason == SkillBundleValidationProblemReason.ToolNotAvailable;

    /// <summary>
    /// Human-readable rendering of a warning, a little more actionable than
    /// the exception-side formatting: explicitly tells the operator what
    /// happens at runtime if the agent tries to call the missing tool.
    /// </summary>
    private static string FormatWarning(SkillBundleValidationProblem problem) =>
        problem.Reason switch
        {
            SkillBundleValidationProblemReason.ToolNotAvailable =>
                $"bundle '{problem.PackageName}/{problem.SkillName}' requires tool '{problem.ToolName}', "
                + "which is not surfaced by any registered connector; "
                + "the agent may get a 'tool not found' error if it tries to call it.",
            _ => problem.ToString(),
        };

    /// <summary>
    /// Mirrors the evaluation logic in <see cref="DefaultUnitPolicyEnforcer"/>:
    /// a tool in <see cref="SkillPolicy.Blocked"/> is always denied; a non-null
    /// <see cref="SkillPolicy.Allowed"/> acts as a whitelist.
    /// </summary>
    private static bool IsBlocked(SkillPolicy policy, string toolName)
    {
        if (policy.Blocked is { Count: > 0 } blocked
            && blocked.Any(b => string.Equals(b, toolName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (policy.Allowed is { } allowed
            && !allowed.Any(a => string.Equals(a, toolName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }
}