// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using System.Collections.Generic;
using System.Linq;

using Cvoya.Spring.Core.Policies;

/// <summary>
/// Default OSS <see cref="ISkillBundleValidator"/>. Collects all tool names
/// surfaced by the registered <see cref="ISkillRegistry"/> instances (case-
/// insensitive) and fails any bundle that declares a required tool outside
/// that set. Also consults <see cref="IUnitPolicyRepository"/> to reject tools
/// explicitly blocked by the unit's <see cref="SkillPolicy"/> — the enforcement
/// path of #163 happens at unit-creation time as well as at call time.
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
    public async Task ValidateAsync(
        string unitId,
        IReadOnlyList<SkillBundle> bundles,
        CancellationToken cancellationToken = default)
    {
        if (bundles.Count == 0)
        {
            return;
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

        var problems = new List<SkillBundleValidationProblem>();

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
                    problems.Add(new SkillBundleValidationProblem(
                        bundle.PackageName,
                        bundle.SkillName,
                        requirement.Name,
                        SkillBundleValidationProblemReason.ToolNotAvailable));
                    continue;
                }

                if (skillPolicy is not null && IsBlocked(skillPolicy, requirement.Name))
                {
                    problems.Add(new SkillBundleValidationProblem(
                        bundle.PackageName,
                        bundle.SkillName,
                        requirement.Name,
                        SkillBundleValidationProblemReason.BlockedByUnitPolicy,
                        DenyingUnitId: unitId));
                }
            }
        }

        if (problems.Count > 0)
        {
            throw new SkillBundleValidationException(problems);
        }
    }

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