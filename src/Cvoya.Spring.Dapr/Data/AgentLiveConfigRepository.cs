// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using System.Text.Json;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF-backed implementation of <see cref="IAgentLiveConfigRepository"/>.
/// Reads and writes <see cref="AgentLiveConfigEntity"/> and
/// <see cref="AgentExpertiseEntity"/> rows; the <c>SpringDbContext</c>
/// stamps <c>TenantId</c> from the ambient <c>ITenantContext</c> on
/// insert and applies the per-entity tenant query filter on read. The
/// per-agent tool-grant rows live in <c>agent_tool_grants</c> and are
/// owned by <see cref="Cvoya.Spring.Core.Skills.IToolGrantResolver"/>;
/// this repository no longer touches them after the #2360 cleanup
/// dropped the legacy operator-skills write path.
/// </summary>
/// <remarks>
/// ADR-0067 §2 (#3111): an agent's <c>model</c> has a single writable home —
/// the agent jsonb <c>execution.model{provider,id}</c> (the dispatch source).
/// <see cref="AgentMetadata.Model"/> is therefore <b>projected</b> from that
/// jsonb (surfacing the model <c>id</c>) on read and is never written to
/// <c>agent_live_config</c>; the structured-model write surface is
/// <c>PUT /agents/{id}/execution</c> (<see cref="IAgentExecutionStore"/>).
/// <c>agent_live_config</c> owns only specialty / enabled / execution-mode /
/// expertise / lifecycle-status.
/// </remarks>
public class AgentLiveConfigRepository(SpringDbContext context) : IAgentLiveConfigRepository
{
    /// <inheritdoc />
    public async Task<AgentMetadata> GetMetadataAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var row = await context.AgentLiveConfigs
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.AgentId == agentId, cancellationToken);

        // ADR-0067 §2: Model projects from the agent jsonb execution block
        // (the single home + dispatch source), not from agent_live_config.
        var model = await ReadJsonbModelIdAsync(agentId, cancellationToken);

        if (row is null)
        {
            // No live-config row yet — every live-config field is unset.
            // Model still comes from the jsonb (which may carry one even
            // when the live-config row was never materialised). Returning
            // unset live-config fields lets the API layer apply its own
            // defaults (Enabled defaulting to true, etc.).
            return new AgentMetadata(Model: model);
        }

        return new AgentMetadata(
            Model: model,
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
        // list on the activity event. ADR-0067 §2: Model is NOT a
        // live-config field — its single home is the agent jsonb (set via
        // the execution PUT), so a Model on this metadata is ignored here.
        var written = new List<string>();
        if (metadata.Specialty is not null) written.Add(nameof(metadata.Specialty));
        if (metadata.Enabled is not null) written.Add(nameof(metadata.Enabled));
        if (metadata.ExecutionMode is not null) written.Add(nameof(metadata.ExecutionMode));

        if (written.Count == 0)
        {
            // No-op patch (every live-config field null; Model and
            // ParentUnit are ignored — the jsonb / membership own them).
            // Don't materialise a row.
            return Array.Empty<string>();
        }

        var row = await context.AgentLiveConfigs
            .FirstOrDefaultAsync(c => c.AgentId == agentId, cancellationToken);

        if (row is null)
        {
            row = new AgentLiveConfigEntity { AgentId = agentId };
            context.AgentLiveConfigs.Add(row);
        }

        if (metadata.Specialty is not null) row.Specialty = metadata.Specialty;
        if (metadata.Enabled is not null) row.Enabled = metadata.Enabled.Value;
        if (metadata.ExecutionMode is not null) row.ExecutionMode = metadata.ExecutionMode.Value;

        await context.SaveChangesAsync(cancellationToken);
        return written;
    }

    /// <summary>
    /// Reads the agent's persisted <c>execution.model.id</c> from the agent
    /// jsonb (ADR-0067 §2). Returns <c>null</c> when the agent has no row,
    /// no execution block, or no structured model. The flat <c>id</c> is the
    /// value that flows to <see cref="AgentMetadata.Model"/> — matching the
    /// model-policy <c>evaluateModel</c> + effective-metadata contract that
    /// previously read the (id-shaped) <c>agent_live_config.model</c>.
    /// </summary>
    private async Task<string?> ReadJsonbModelIdAsync(Guid agentId, CancellationToken cancellationToken)
    {
        var definition = await context.AgentDefinitions
            .AsNoTracking()
            .Where(a => a.Id == agentId && a.DeletedAt == null)
            .Select(a => a.Definition)
            .FirstOrDefaultAsync(cancellationToken);

        if (definition is not { ValueKind: JsonValueKind.Object } element
            || !element.TryGetProperty("execution", out var exec)
            || exec.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return ExecutionJson.ReadModel(exec)?.Id;
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
