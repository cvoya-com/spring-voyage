// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Agents;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of
/// <see cref="IAgentStateCoordinator"/>. Owns the persisted-config CRUD
/// concern extracted from <c>AgentActor</c>: reading and writing the
/// agent's metadata, skills, and expertise domains, and emitting the
/// corresponding <see cref="ActivityEventType.StateChanged"/> activity
/// events.
/// </summary>
/// <remarks>
/// Per ADR-0040 / #2048 every read and write goes through
/// <see cref="IAgentLiveConfigStore"/>, which in turn drives the
/// <c>agent_live_config</c>, <c>agent_skill_grants</c>, and
/// <c>agent_expertise</c> EF tables. There is no actor-state copy.
/// </remarks>
public class AgentStateCoordinator(
    IAgentLiveConfigStore liveConfigStore,
    ILogger<AgentStateCoordinator> logger) : IAgentStateCoordinator
{
    /// <inheritdoc />
    public async Task<AgentMetadata> GetMetadataAsync(
        string agentId, CancellationToken cancellationToken = default)
    {
        if (!GuidFormatter.TryParse(agentId, out var agentGuid))
        {
            // Legacy / non-UUID actor id — there is no EF row to read.
            // Returning all-null lets API surfaces apply their own
            // defaults the same way they would for a row-less agent.
            return new AgentMetadata();
        }

        return await liveConfigStore.GetMetadataAsync(agentGuid, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetMetadataAsync(
        string agentId,
        AgentMetadata metadata,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(emitActivity);

        if (!GuidFormatter.TryParse(agentId, out var agentGuid))
        {
            logger.LogWarning(
                "Agent {AgentId} SetMetadata received non-UUID actor id; cannot persist EF state.",
                agentId);
            return;
        }

        var written = await liveConfigStore.UpsertMetadataAsync(agentGuid, metadata, cancellationToken);

        if (written.Count == 0)
        {
            logger.LogDebug(
                "Agent {AgentId} SetMetadataAsync called with no fields to write; nothing to emit.",
                agentId);
            return;
        }

        logger.LogInformation(
            "Agent {AgentId} metadata updated: {Fields}",
            agentId, string.Join(",", written));

        await emitActivity(
            BuildEvent(
                agentId,
                ActivityEventType.StateChanged,
                ActivitySeverity.Debug,
                $"Agent metadata updated: {string.Join(", ", written)}",
                details: JsonSerializer.SerializeToElement(new
                {
                    action = "AgentMetadataUpdated",
                    fields = written,
                    model = metadata.Model,
                    specialty = metadata.Specialty,
                    enabled = metadata.Enabled,
                    executionMode = metadata.ExecutionMode?.ToString(),
                })),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string[]> GetSkillsAsync(
        string agentId, CancellationToken cancellationToken = default)
    {
        if (!GuidFormatter.TryParse(agentId, out var agentGuid))
        {
            return [];
        }

        return await liveConfigStore.GetSkillsAsync(agentGuid, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetSkillsAsync(
        string agentId,
        string[] skills,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skills);
        ArgumentNullException.ThrowIfNull(emitActivity);

        if (!GuidFormatter.TryParse(agentId, out var agentGuid))
        {
            logger.LogWarning(
                "Agent {AgentId} SetSkills received non-UUID actor id; cannot persist EF state.",
                agentId);
            return;
        }

        var persisted = await liveConfigStore.SetSkillsAsync(agentGuid, skills, cancellationToken);

        await emitActivity(
            BuildEvent(
                agentId,
                ActivityEventType.StateChanged,
                ActivitySeverity.Debug,
                $"Agent skills replaced: {persisted.Length} skill(s).",
                details: JsonSerializer.SerializeToElement(new
                {
                    action = "AgentSkillsReplaced",
                    count = persisted.Length,
                    skills = persisted,
                })),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ExpertiseDomain[]> GetExpertiseAsync(
        string agentId, CancellationToken cancellationToken = default)
    {
        if (!GuidFormatter.TryParse(agentId, out var agentGuid))
        {
            return [];
        }

        return await liveConfigStore.GetExpertiseAsync(agentGuid, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetExpertiseAsync(
        string agentId,
        ExpertiseDomain[] domains,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domains);
        ArgumentNullException.ThrowIfNull(emitActivity);

        if (!GuidFormatter.TryParse(agentId, out var agentGuid))
        {
            logger.LogWarning(
                "Agent {AgentId} SetExpertise received non-UUID actor id; cannot persist EF state.",
                agentId);
            return;
        }

        var persisted = await liveConfigStore.SetExpertiseAsync(agentGuid, domains, cancellationToken);

        await emitActivity(
            BuildEvent(
                agentId,
                ActivityEventType.StateChanged,
                ActivitySeverity.Debug,
                $"Agent expertise replaced: {persisted.Length} domain(s).",
                details: JsonSerializer.SerializeToElement(new
                {
                    action = "AgentExpertiseReplaced",
                    count = persisted.Length,
                    domains = persisted.Select(d => new { d.Name, d.Description, Level = d.Level?.ToString() }),
                })),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<bool> HasExpertiseSetAsync(
        string agentId, CancellationToken cancellationToken = default)
    {
        if (!GuidFormatter.TryParse(agentId, out var agentGuid))
        {
            return false;
        }

        return await liveConfigStore.HasExpertiseSetAsync(agentGuid, cancellationToken);
    }

    private static ActivityEvent BuildEvent(
        string agentId,
        ActivityEventType eventType,
        ActivitySeverity severity,
        string summary,
        JsonElement? details = null,
        string? correlationId = null)
    {
        return new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            Core.Messaging.Address.For("agent", agentId),
            eventType,
            severity,
            summary,
            details,
            correlationId);
    }
}
