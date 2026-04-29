// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Initiative;

using System.Text.Json;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="IAgentStateCoordinator"/>.
/// Owns the persisted-config CRUD concern extracted from <c>AgentActor</c>:
/// reading and writing the agent's metadata, skills, and expertise domains,
/// and emitting the corresponding <see cref="ActivityEventType.StateChanged"/>
/// activity events.
/// </summary>
/// <remarks>
/// The coordinator is stateless with respect to any individual agent — it
/// operates entirely through the per-call delegates. This makes it safe to
/// register as a singleton and share across all <c>AgentActor</c> instances.
/// </remarks>
public class AgentStateCoordinator(
    ILogger<AgentStateCoordinator> logger) : IAgentStateCoordinator
{
    /// <inheritdoc />
    public async Task<AgentMetadata> GetMetadataAsync(
        string agentId,
        Func<CancellationToken, Task<(bool hasValue, string? value)>> getModel,
        Func<CancellationToken, Task<(bool hasValue, string? value)>> getSpecialty,
        Func<CancellationToken, Task<(bool hasValue, bool value)>> getEnabled,
        Func<CancellationToken, Task<(bool hasValue, AgentExecutionMode value)>> getExecutionMode,
        Func<CancellationToken, Task<(bool hasValue, string? value)>> getParentUnit,
        CancellationToken cancellationToken = default)
    {
        var (hasModel, model) = await getModel(cancellationToken);
        var (hasSpecialty, specialty) = await getSpecialty(cancellationToken);
        var (hasEnabled, enabled) = await getEnabled(cancellationToken);
        var (hasExecutionMode, executionMode) = await getExecutionMode(cancellationToken);
        var (hasParentUnit, parentUnit) = await getParentUnit(cancellationToken);

        return new AgentMetadata(
            Model: hasModel ? model : null,
            Specialty: hasSpecialty ? specialty : null,
            Enabled: hasEnabled ? enabled : null,
            ExecutionMode: hasExecutionMode ? executionMode : null,
            ParentUnit: hasParentUnit ? parentUnit : null);
    }

    /// <inheritdoc />
    public async Task SetMetadataAsync(
        string agentId,
        AgentMetadata metadata,
        Func<string, CancellationToken, Task> setModel,
        Func<string, CancellationToken, Task> setSpecialty,
        Func<bool, CancellationToken, Task> setEnabled,
        Func<AgentExecutionMode, CancellationToken, Task> setExecutionMode,
        Func<string, CancellationToken, Task> setParentUnit,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var writtenFields = new List<string>();

        if (metadata.Model is not null)
        {
            await setModel(metadata.Model, cancellationToken);
            writtenFields.Add(nameof(metadata.Model));
        }

        if (metadata.Specialty is not null)
        {
            await setSpecialty(metadata.Specialty, cancellationToken);
            writtenFields.Add(nameof(metadata.Specialty));
        }

        if (metadata.Enabled is not null)
        {
            await setEnabled(metadata.Enabled.Value, cancellationToken);
            writtenFields.Add(nameof(metadata.Enabled));
        }

        if (metadata.ExecutionMode is not null)
        {
            await setExecutionMode(metadata.ExecutionMode.Value, cancellationToken);
            writtenFields.Add(nameof(metadata.ExecutionMode));
        }

        if (metadata.ParentUnit is not null)
        {
            await setParentUnit(metadata.ParentUnit, cancellationToken);
            writtenFields.Add(nameof(metadata.ParentUnit));
        }

        if (writtenFields.Count == 0)
        {
            logger.LogDebug(
                "Agent {AgentId} SetMetadataAsync called with no fields; nothing to emit.",
                agentId);
            return;
        }

        logger.LogInformation(
            "Agent {AgentId} metadata updated: {Fields}",
            agentId, string.Join(",", writtenFields));

        await emitActivity(
            BuildEvent(
                agentId,
                ActivityEventType.StateChanged,
                ActivitySeverity.Debug,
                $"Agent metadata updated: {string.Join(", ", writtenFields)}",
                details: JsonSerializer.SerializeToElement(new
                {
                    action = "AgentMetadataUpdated",
                    fields = writtenFields,
                    model = metadata.Model,
                    specialty = metadata.Specialty,
                    enabled = metadata.Enabled,
                    executionMode = metadata.ExecutionMode?.ToString(),
                    parentUnit = metadata.ParentUnit,
                })),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearParentUnitAsync(
        string agentId,
        Func<CancellationToken, Task> removeParentUnit,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default)
    {
        await removeParentUnit(cancellationToken);

        logger.LogInformation("Agent {AgentId} parent-unit pointer cleared.", agentId);

        await emitActivity(
            BuildEvent(
                agentId,
                ActivityEventType.StateChanged,
                ActivitySeverity.Debug,
                "Agent parent-unit cleared",
                details: JsonSerializer.SerializeToElement(new
                {
                    action = "AgentParentUnitCleared",
                })),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<string[]> GetSkillsAsync(
        string agentId,
        Func<CancellationToken, Task<(bool hasValue, List<string>? value)>> getSkills,
        CancellationToken cancellationToken = default)
    {
        var (hasValue, value) = await getSkills(cancellationToken);
        return hasValue && value is not null ? value.ToArray() : [];
    }

    /// <inheritdoc />
    public async Task SetSkillsAsync(
        string agentId,
        string[] skills,
        Func<List<string>, CancellationToken, Task> setSkills,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skills);

        // Canonicalise: drop null / whitespace entries, collapse duplicates,
        // sort. Ordering is semantically meaningless — the list is a set —
        // but a stable order makes diffs in logs and activity events
        // predictable.
        var normalised = skills
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        await setSkills(normalised, cancellationToken);

        logger.LogInformation(
            "Agent {AgentId} skills replaced. Count: {Count}", agentId, normalised.Count);

        await emitActivity(
            BuildEvent(
                agentId,
                ActivityEventType.StateChanged,
                ActivitySeverity.Debug,
                $"Agent skills replaced: {normalised.Count} skill(s).",
                details: JsonSerializer.SerializeToElement(new
                {
                    action = "AgentSkillsReplaced",
                    count = normalised.Count,
                    skills = normalised,
                })),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<ExpertiseDomain[]> GetExpertiseAsync(
        string agentId,
        Func<CancellationToken, Task<(bool hasValue, List<ExpertiseDomain>? value)>> getExpertise,
        CancellationToken cancellationToken = default)
    {
        var (hasValue, value) = await getExpertise(cancellationToken);
        return hasValue && value is not null ? value.ToArray() : Array.Empty<ExpertiseDomain>();
    }

    /// <inheritdoc />
    public async Task SetExpertiseAsync(
        string agentId,
        ExpertiseDomain[] domains,
        Func<List<ExpertiseDomain>, CancellationToken, Task> setExpertise,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(domains);

        // De-dup by domain name case-insensitively; the last write for a given
        // name wins so a caller can PATCH a level or description by re-listing
        // the same domain.
        var normalised = domains
            .Where(d => !string.IsNullOrWhiteSpace(d.Name))
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last())
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToList();

        await setExpertise(normalised, cancellationToken);

        logger.LogInformation(
            "Agent {AgentId} expertise replaced. Count: {Count}", agentId, normalised.Count);

        await emitActivity(
            BuildEvent(
                agentId,
                ActivityEventType.StateChanged,
                ActivitySeverity.Debug,
                $"Agent expertise replaced: {normalised.Count} domain(s).",
                details: JsonSerializer.SerializeToElement(new
                {
                    action = "AgentExpertiseReplaced",
                    count = normalised.Count,
                    domains = normalised.Select(d => new { d.Name, d.Description, Level = d.Level?.ToString() }),
                })),
            cancellationToken);
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
            new Core.Messaging.Address("agent", agentId),
            eventType,
            severity,
            summary,
            details,
            correlationId);
    }
}