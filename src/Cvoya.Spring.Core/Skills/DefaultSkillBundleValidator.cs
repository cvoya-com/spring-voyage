// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using System.Collections.Generic;
using System.Linq;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Policies;

/// <summary>
/// Default OSS <see cref="ISkillBundleValidator"/>. Validates each
/// <see cref="SkillBundle.RequiredTools"/> entry against the four-tier tool
/// surface assembled by the grant resolver (#2335):
///
/// 1. Registry tier — any <see cref="ISkillRegistry"/> exposing tools in the
///    declaration's namespace via <see cref="ISkillRegistry.GetToolsByNamespace"/>.
///    Resolution is namespace-scoped: if a registry exposes any tool under the
///    declaration's namespace, the requirement is considered satisfiable (the
///    grant resolver lands the whole namespace on units that bind the matching
///    connector).
/// 2. Image tier — fallback to <see cref="IImageToolsReader.GetImageToolsAsync"/>
///    for the unit being created when no registry exposes the namespace. An
///    SDK-introspected image tool with the exact <see cref="SkillToolRequirement.Name"/>
///    resolves the requirement.
///
/// Failure modes:
/// * <see cref="SkillBundleValidationProblemReason.ToolNotAvailable"/> — no
///   registry exposes the namespace AND no image-tier source provides the
///   tool name. **Strict (#2346)**: this is blocking. The OSS lenient
///   "log warning, continue" path from Sub B (#2335) has been removed —
///   operators see the misconfiguration at install time rather than as a
///   runtime "tool not found" the agent stumbles into.
/// * <see cref="SkillBundleValidationProblemReason.BlockedByUnitPolicy"/> —
///   blocking (the C3 security invariant): a unit's <see cref="SkillPolicy"/>
///   must be honoured at create time as it is at call time.
///
/// Optional requirements (<see cref="SkillToolRequirement.Optional"/> = true)
/// remain advisory: a missing optional tool does not block install. Policy
/// denies still apply — blocked-but-advertised remains a stronger signal
/// than missing.
/// </summary>
public class DefaultSkillBundleValidator : ISkillBundleValidator
{
    private readonly IReadOnlyList<ISkillRegistry> _registries;
    private readonly IUnitPolicyRepository _policyRepository;
    private readonly IImageToolsReader _imageToolsReader;

    /// <summary>
    /// Creates a new <see cref="DefaultSkillBundleValidator"/>.
    /// </summary>
    public DefaultSkillBundleValidator(
        IEnumerable<ISkillRegistry> registries,
        IUnitPolicyRepository policyRepository,
        IImageToolsReader imageToolsReader)
    {
        _registries = registries.ToList();
        _policyRepository = policyRepository;
        _imageToolsReader = imageToolsReader;
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

        var policy = await _policyRepository.GetAsync(unitId, cancellationToken);
        var skillPolicy = policy.Skill;

        // Lazy-evaluated namespace cache so we only ask each registry once per
        // namespace per call (matches the grant-resolver lookup shape).
        var namespaceCache = new Dictionary<string, bool>(StringComparer.Ordinal);

        // Image-tier lookup is keyed by the subject Address; resolve lazily —
        // most validations don't need it because the registry tier covers the
        // declaration.
        IReadOnlyList<ImageToolEntry>? imageTools = null;

        var blocking = new List<SkillBundleValidationProblem>();

        foreach (var bundle in bundles)
        {
            foreach (var requirement in bundle.RequiredTools)
            {
                var resolved = false;

                // (1) Registry tier — namespace presence is sufficient. The
                // grant resolver maps the connector binding onto the full
                // namespace, so any tool under the declaration's namespace
                // proves the surface is reachable when the matching binding
                // is in place at runtime.
                var ns = ToolNaming.GetNamespace(requirement.Name);
                if (!string.IsNullOrEmpty(ns) && IsNamespaceRegistered(ns, namespaceCache))
                {
                    resolved = true;
                }

                // (2) Image tier — SDK-introspected tools live on the
                // subject's image_tools column. Only consulted when the
                // registry tier didn't match; mirrors the grant-resolver
                // additive-merge order.
                if (!resolved)
                {
                    imageTools ??= await _imageToolsReader
                        .GetImageToolsAsync(
                            Address.ForIdentity(Address.UnitScheme, unitId),
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (imageTools.Any(t =>
                        string.Equals(t.Name, requirement.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        resolved = true;
                    }
                }

                if (!resolved)
                {
                    if (requirement.Optional)
                    {
                        // Optional requirements stay advisory — a missing
                        // optional tool does not block install. Policy
                        // checks below are skipped: there's nothing
                        // registered to evaluate against.
                        continue;
                    }

                    blocking.Add(new SkillBundleValidationProblem(
                        bundle.PackageName,
                        bundle.SkillName,
                        requirement.Name,
                        SkillBundleValidationProblemReason.ToolNotAvailable));
                    continue;
                }

                if (skillPolicy is not null && IsBlocked(skillPolicy, requirement.Name))
                {
                    blocking.Add(new SkillBundleValidationProblem(
                        bundle.PackageName,
                        bundle.SkillName,
                        requirement.Name,
                        SkillBundleValidationProblemReason.BlockedByUnitPolicy,
                        DenyingUnitId: unitId.ToString()));
                }
            }
        }

        if (blocking.Count > 0)
        {
            throw new SkillBundleValidationException(blocking);
        }

        return SkillBundleValidationReport.Empty;
    }

    /// <summary>
    /// Returns true when at least one registered <see cref="ISkillRegistry"/>
    /// exposes a tool under <paramref name="namespace"/>. Memoises results in
    /// <paramref name="cache"/> so the per-call namespace lookup stays O(1)
    /// when multiple requirements share a namespace.
    /// </summary>
    private bool IsNamespaceRegistered(string @namespace, Dictionary<string, bool> cache)
    {
        if (cache.TryGetValue(@namespace, out var cached))
        {
            return cached;
        }

        var found = false;
        foreach (var registry in _registries)
        {
            if (registry.GetToolsByNamespace(@namespace).Count > 0)
            {
                found = true;
                break;
            }
        }

        cache[@namespace] = found;
        return found;
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
