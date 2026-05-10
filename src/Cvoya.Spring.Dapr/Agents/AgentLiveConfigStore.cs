// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Agents;

using System.Diagnostics;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Dapr.Data;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="IAgentLiveConfigStore"/>.
/// Creates a fresh <c>IServiceScope</c> per call so the scoped
/// <see cref="IAgentLiveConfigRepository"/> (and its
/// <c>SpringDbContext</c>) resolves cleanly from the agent actor's
/// singleton-style activation. Logs the wall-clock duration of every
/// activation-path read (<see cref="GetMetadataAsync"/>,
/// <see cref="GetSkillsAsync"/>, <see cref="GetExpertiseAsync"/>,
/// <see cref="HasExpertiseSetAsync"/>) so the v0.2 cache decision is
/// data-driven (ADR-0040 § 3).
/// </summary>
public class AgentLiveConfigStore(
    IServiceScopeFactory scopeFactory,
    ILogger<AgentLiveConfigStore> logger) : IAgentLiveConfigStore
{
    /// <inheritdoc />
    public async Task<AgentMetadata> GetMetadataAsync(
        Guid agentId, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAgentLiveConfigRepository>();
        var result = await repo.GetMetadataAsync(agentId, cancellationToken);
        sw.Stop();
        logger.LogDebug(
            "AgentLiveConfig.GetMetadata agent={AgentId} elapsedMs={ElapsedMs}",
            agentId, sw.Elapsed.TotalMilliseconds);
        return result;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>> UpsertMetadataAsync(
        Guid agentId, AgentMetadata metadata, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAgentLiveConfigRepository>();
        var written = await repo.UpsertMetadataAsync(agentId, metadata, cancellationToken);
        if (written.Count > 0)
        {
            logger.LogInformation(
                "Agent {AgentId} live-config updated: {Fields}",
                agentId, string.Join(",", written));
        }
        return written;
    }

    /// <inheritdoc />
    public async Task<string[]> GetSkillsAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAgentLiveConfigRepository>();
        var result = await repo.GetSkillsAsync(agentId, cancellationToken);
        sw.Stop();
        logger.LogDebug(
            "AgentLiveConfig.GetSkills agent={AgentId} count={Count} elapsedMs={ElapsedMs}",
            agentId, result.Length, sw.Elapsed.TotalMilliseconds);
        return result;
    }

    /// <inheritdoc />
    public async Task<string[]> SetSkillsAsync(
        Guid agentId, IReadOnlyList<string> skills, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAgentLiveConfigRepository>();
        var persisted = await repo.SetSkillsAsync(agentId, skills, cancellationToken);
        logger.LogInformation(
            "Agent {AgentId} skills replaced. Count: {Count}", agentId, persisted.Length);
        return persisted;
    }

    /// <inheritdoc />
    public async Task<ExpertiseDomain[]> GetExpertiseAsync(
        Guid agentId, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAgentLiveConfigRepository>();
        var result = await repo.GetExpertiseAsync(agentId, cancellationToken);
        sw.Stop();
        logger.LogDebug(
            "AgentLiveConfig.GetExpertise agent={AgentId} count={Count} elapsedMs={ElapsedMs}",
            agentId, result.Length, sw.Elapsed.TotalMilliseconds);
        return result;
    }

    /// <inheritdoc />
    public async Task<ExpertiseDomain[]> SetExpertiseAsync(
        Guid agentId, IReadOnlyList<ExpertiseDomain> domains, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAgentLiveConfigRepository>();
        var persisted = await repo.SetExpertiseAsync(agentId, domains, cancellationToken);
        logger.LogInformation(
            "Agent {AgentId} expertise replaced. Count: {Count}", agentId, persisted.Length);
        return persisted;
    }

    /// <inheritdoc />
    public async Task<bool> HasExpertiseSetAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<IAgentLiveConfigRepository>();
        var result = await repo.HasExpertiseSetAsync(agentId, cancellationToken);
        sw.Stop();
        logger.LogDebug(
            "AgentLiveConfig.HasExpertiseSet agent={AgentId} value={Value} elapsedMs={ElapsedMs}",
            agentId, result, sw.Elapsed.TotalMilliseconds);
        return result;
    }
}
