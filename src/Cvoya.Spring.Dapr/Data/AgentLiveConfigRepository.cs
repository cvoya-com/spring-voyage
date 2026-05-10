// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF-backed implementation of <see cref="IAgentLiveConfigRepository"/>.
/// Reads and writes <see cref="AgentLiveConfigEntity"/>,
/// <see cref="AgentSkillGrantEntity"/>, and
/// <see cref="AgentExpertiseEntity"/> rows; the <c>SpringDbContext</c>
/// stamps <c>TenantId</c> from the ambient <c>ITenantContext</c> on
/// insert and applies the per-entity tenant query filter on read.
/// </summary>
public class AgentLiveConfigRepository(SpringDbContext context) : IAgentLiveConfigRepository
{
    /// <inheritdoc />
    public async Task<AgentMetadata> GetMetadataAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var row = await context.AgentLiveConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.AgentId == agentId, cancellationToken);

        if (row is null)
        {
            // No row yet — every field is unset. Returning all-null lets
            // the API layer apply its own defaults (Enabled defaulting
            // to true, etc.) without the repository taking a position.
            return new AgentMetadata();
        }

        return new AgentMetadata(
            Model: row.Model,
            Specialty: row.Specialty,
            Enabled: row.Enabled,
            ExecutionMode: row.ExecutionMode,
            ParentUnit: null);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> UpsertMetadataAsync(
        Guid agentId, AgentMetadata metadata, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        // Identify which fields are actually being patched first so we
        // can both decide whether to write and emit an accurate field
        // list on the activity event.
        var written = new List<string>();
        if (metadata.Model is not null) written.Add(nameof(metadata.Model));
        if (metadata.Specialty is not null) written.Add(nameof(metadata.Specialty));
        if (metadata.Enabled is not null) written.Add(nameof(metadata.Enabled));
        if (metadata.ExecutionMode is not null) written.Add(nameof(metadata.ExecutionMode));

        if (written.Count == 0)
        {
            // No-op patch (every field null and ParentUnit is ignored —
            // membership owns it). Don't materialise a row.
            return Array.Empty<string>();
        }

        var row = await context.AgentLiveConfigs
            .FirstOrDefaultAsync(c => c.AgentId == agentId, cancellationToken);

        if (row is null)
        {
            row = new AgentLiveConfigEntity { AgentId = agentId };
            context.AgentLiveConfigs.Add(row);
        }

        if (metadata.Model is not null) row.Model = metadata.Model;
        if (metadata.Specialty is not null) row.Specialty = metadata.Specialty;
        if (metadata.Enabled is not null) row.Enabled = metadata.Enabled.Value;
        if (metadata.ExecutionMode is not null) row.ExecutionMode = metadata.ExecutionMode.Value;

        await context.SaveChangesAsync(cancellationToken);
        return written;
    }

    /// <inheritdoc />
    public async Task<string[]> GetSkillsAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        return await context.AgentSkillGrants
            .AsNoTracking()
            .Where(s => s.AgentId == agentId)
            .OrderBy(s => s.SkillName)
            .Select(s => s.SkillName)
            .ToArrayAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string[]> SetSkillsAsync(
        Guid agentId, IReadOnlyList<string> skills, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skills);

        // Canonicalise the input: drop blank entries, trim, dedupe (case-
        // insensitive — same skill name in different casing is the same
        // grant), sort. A stable order makes diffs in logs and activity
        // events predictable.
        var normalised = skills
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();

        var existing = await context.AgentSkillGrants
            .Where(s => s.AgentId == agentId)
            .ToListAsync(cancellationToken);

        var keep = new HashSet<string>(normalised, StringComparer.OrdinalIgnoreCase);

        // Remove rows that are no longer in the list.
        foreach (var row in existing)
        {
            if (!keep.Contains(row.SkillName))
            {
                context.AgentSkillGrants.Remove(row);
            }
        }

        // Add rows for skills that aren't yet persisted.
        var existingNames = new HashSet<string>(
            existing.Select(s => s.SkillName), StringComparer.OrdinalIgnoreCase);

        var now = DateTimeOffset.UtcNow;
        foreach (var skill in normalised)
        {
            if (!existingNames.Contains(skill))
            {
                context.AgentSkillGrants.Add(new AgentSkillGrantEntity
                {
                    Id = Guid.NewGuid(),
                    AgentId = agentId,
                    SkillName = skill,
                    GrantedAt = now,
                });
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        return normalised;
    }

    /// <inheritdoc />
    public async Task<ExpertiseDomain[]> GetExpertiseAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var rows = await context.AgentExpertise
            .AsNoTracking()
            .Where(e => e.AgentId == agentId)
            .OrderBy(e => e.Name)
            .ToArrayAsync(cancellationToken);

        return [.. rows.Select(ToDomain)];
    }

    /// <inheritdoc />
    public async Task<ExpertiseDomain[]> SetExpertiseAsync(
        Guid agentId, IReadOnlyList<ExpertiseDomain> domains, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domains);

        // Normalise: drop blank-name entries, group by name case-
        // insensitively (last write wins so a caller can patch level /
        // description by re-listing the same domain), sort by name.
        var normalised = domains
            .Where(d => !string.IsNullOrWhiteSpace(d.Name))
            .GroupBy(d => d.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last() with { Name = g.Key })
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToArray();

        var existing = await context.AgentExpertise
            .Where(e => e.AgentId == agentId)
            .ToListAsync(cancellationToken);

        // Group existing rows by name for fast lookup; collisions are
        // impossible thanks to the unique index but be defensive.
        var existingByName = existing
            .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        var keep = new HashSet<string>(normalised.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);

        // Remove rows whose names dropped out of the new list.
        foreach (var row in existing)
        {
            if (!keep.Contains(row.Name))
            {
                context.AgentExpertise.Remove(row);
            }
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var domain in normalised)
        {
            if (existingByName.TryGetValue(domain.Name, out var row))
            {
                // Update in place — preserve the original CreatedAt so
                // an audit reader can tell when the agent first declared
                // this domain. The audit trail of mutations lives in
                // the activity-event stream.
                row.Description = domain.Description ?? string.Empty;
                row.Level = domain.Level;
                row.InputSchemaJson = domain.InputSchemaJson;
            }
            else
            {
                context.AgentExpertise.Add(new AgentExpertiseEntity
                {
                    Id = Guid.NewGuid(),
                    AgentId = agentId,
                    Name = domain.Name,
                    Description = domain.Description ?? string.Empty,
                    Level = domain.Level,
                    InputSchemaJson = domain.InputSchemaJson,
                    CreatedAt = now,
                });
            }
        }

        // Mark the live-config row as "expertise initialised" so the
        // activation seeder honours the actor-state-wins precedence rule
        // even when the persisted list is empty.
        var liveConfig = await context.AgentLiveConfigs
            .FirstOrDefaultAsync(c => c.AgentId == agentId, cancellationToken);
        if (liveConfig is null)
        {
            liveConfig = new AgentLiveConfigEntity { AgentId = agentId };
            context.AgentLiveConfigs.Add(liveConfig);
        }
        liveConfig.ExpertiseInitialised = true;

        await context.SaveChangesAsync(cancellationToken);
        return normalised;
    }

    /// <inheritdoc />
    public async Task<bool> HasExpertiseSetAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        return await context.AgentLiveConfigs
            .AsNoTracking()
            .Where(c => c.AgentId == agentId)
            .Select(c => c.ExpertiseInitialised)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static ExpertiseDomain ToDomain(AgentExpertiseEntity e) =>
        new(Name: e.Name, Description: e.Description, Level: e.Level, InputSchemaJson: e.InputSchemaJson);
}
