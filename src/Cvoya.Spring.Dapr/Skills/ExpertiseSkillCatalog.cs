// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IExpertiseSkillCatalog"/>: derives the skill surface
/// from the expertise directory on every call (#359). Unit-scope entries are
/// read through <see cref="IExpertiseAggregator"/> with the supplied
/// <see cref="BoundaryViewContext"/>, so outside callers already get the
/// boundary-filtered view shipped by #413 / #497; agent-scope contributors
/// are visible only when the caller is inside the boundary. No snapshot —
/// directory mutations propagate on the next enumeration.
/// </summary>
/// <remarks>
/// <para>
/// This type is a singleton but does not itself cache. The underlying
/// <see cref="IExpertiseAggregator"/> handles caching + invalidation (per
/// ADR 0006), which is exactly the source-of-truth property the rework
/// requires — no parallel capability registry to keep in sync.
/// </para>
/// </remarks>
public class ExpertiseSkillCatalog(
    IDirectoryService directoryService,
    IExpertiseAggregator aggregator,
    ILoggerFactory loggerFactory) : IExpertiseSkillCatalog
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ExpertiseSkillCatalog>();

    /// <inheritdoc />
    public async Task<IReadOnlyList<ExpertiseSkill>> EnumerateAsync(
        BoundaryViewContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        IReadOnlyList<DirectoryEntry> all;
        try
        {
            all = await directoryService.ListAllAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ExpertiseSkillCatalog: directory ListAll failed; returning empty.");
            return Array.Empty<ExpertiseSkill>();
        }

        // We key the surfaced skills by (skill-name, target) so the same
        // expertise slug reachable via multiple units (e.g. a leaf agent that
        // is a member of two parents) doesn't double-up.
        var result = new Dictionary<string, ExpertiseSkill>(StringComparer.Ordinal);

        foreach (var entry in all)
        {
            if (!string.Equals(entry.Address.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AggregatedExpertise aggregated;
            try
            {
                aggregated = await aggregator.GetAsync(entry.Address, context, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "ExpertiseSkillCatalog: aggregation failed for unit {Address}; skipping.", entry.Address);
                continue;
            }

            foreach (var expertise in aggregated.Entries)
            {
                if (expertise.Domain.InputSchemaJson is null)
                {
                    // Consultative-only — no typed contract, not skill-callable.
                    continue;
                }

                // Outside callers should only see unit-projected expertise.
                // Inside callers see everything inside the boundary. The
                // boundary decorator ships from #413/#497: for external
                // contexts, boundary-filtered `Entries` already reflect the
                // projection; for internal contexts, raw member entries flow
                // through. We additionally hide agent-origin entries from
                // external callers here as a defence in depth, so a unit
                // whose boundary doesn't project agent-level expertise still
                // doesn't leak it to external skill enumeration.
                if (!context.Internal &&
                    !string.Equals(expertise.Origin.Scheme, "unit", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var skillName = ExpertiseSkillNaming.GetSkillName(expertise.Domain);
                if (string.IsNullOrEmpty(skillName) || skillName.Length == ExpertiseSkillNaming.Prefix.Length)
                {
                    // Empty or whitespace-only domain name → no stable slug.
                    continue;
                }

                var tool = new ToolDefinition(
                    skillName,
                    expertise.Domain.Description ?? string.Empty,
                    ExpertiseSkillNaming.ParseSchemaOrEmpty(expertise.Domain.InputSchemaJson),
                    ToolCategories.Expertise);

                var key = skillName + "|" + expertise.Origin.Scheme + "://" + expertise.Origin.Path;
                if (!result.ContainsKey(key))
                {
                    result[key] = new ExpertiseSkill(skillName, tool, expertise.Origin, expertise);
                }
            }
        }

        return result.Values
            .OrderBy(s => s.SkillName, StringComparer.Ordinal)
            .ThenBy(s => s.Target.Path, StringComparer.Ordinal)
            .ToList();
    }

    /// <inheritdoc />
    public async Task<ExpertiseSkill?> ResolveAsync(
        string skillName,
        BoundaryViewContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(skillName))
        {
            return null;
        }

        // Live re-enumeration — the catalog is the source of truth, never a
        // cache. A future optimisation can build an index keyed by
        // skill-name, but the directory churn rate is low and aggregator
        // output is already cached.
        var all = await EnumerateAsync(context, cancellationToken);
        foreach (var skill in all)
        {
            if (string.Equals(skill.SkillName, skillName, StringComparison.Ordinal))
            {
                return skill;
            }
        }
        return null;
    }
}
