// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.TestHelpers;

using System.Collections.Concurrent;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Dapr.Agents;

/// <summary>
/// In-memory test double for <see cref="IAgentLiveConfigStore"/>. Lets
/// unit tests exercise the EF-backed agent live-config / skills /
/// expertise surface without standing up a Postgres / Testcontainer.
/// Cross-restart behaviour is covered by the integration tests with a
/// real <c>SpringDbContext</c>.
/// </summary>
public class InMemoryAgentLiveConfigStore : IAgentLiveConfigStore
{
    private readonly ConcurrentDictionary<Guid, AgentMetadata> _metadata = new();
    private readonly ConcurrentDictionary<Guid, string[]> _skills = new();
    private readonly ConcurrentDictionary<Guid, ExpertiseDomain[]> _expertise = new();
    private readonly ConcurrentDictionary<Guid, bool> _expertiseInitialised = new();

    public Task<AgentMetadata> GetMetadataAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_metadata.TryGetValue(agentId, out var v) ? v : new AgentMetadata());
    }

    public Task<IReadOnlyList<string>> UpsertMetadataAsync(
        Guid agentId, AgentMetadata metadata, CancellationToken cancellationToken = default)
    {
        var written = new List<string>();
        var existing = _metadata.TryGetValue(agentId, out var v) ? v : new AgentMetadata();

        if (metadata.Model is not null)
        {
            existing = existing with { Model = metadata.Model };
            written.Add(nameof(metadata.Model));
        }
        if (metadata.Specialty is not null)
        {
            existing = existing with { Specialty = metadata.Specialty };
            written.Add(nameof(metadata.Specialty));
        }
        if (metadata.Enabled is not null)
        {
            existing = existing with { Enabled = metadata.Enabled };
            written.Add(nameof(metadata.Enabled));
        }
        if (metadata.ExecutionMode is not null)
        {
            existing = existing with { ExecutionMode = metadata.ExecutionMode };
            written.Add(nameof(metadata.ExecutionMode));
        }

        if (written.Count > 0)
        {
            _metadata[agentId] = existing;
        }
        return Task.FromResult<IReadOnlyList<string>>(written);
    }

    public Task<string[]> GetSkillsAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_skills.TryGetValue(agentId, out var v) ? v : []);
    }

    public Task<string[]> SetSkillsAsync(
        Guid agentId, IReadOnlyList<string> skills, CancellationToken cancellationToken = default)
    {
        var normalised = skills
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToArray();
        _skills[agentId] = normalised;
        return Task.FromResult(normalised);
    }

    public Task<ExpertiseDomain[]> GetExpertiseAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_expertise.TryGetValue(agentId, out var v) ? v : []);
    }

    public Task<ExpertiseDomain[]> SetExpertiseAsync(
        Guid agentId, IReadOnlyList<ExpertiseDomain> domains, CancellationToken cancellationToken = default)
    {
        var normalised = domains
            .Where(d => !string.IsNullOrWhiteSpace(d.Name))
            .GroupBy(d => d.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last() with { Name = g.Key })
            .OrderBy(d => d.Name, StringComparer.Ordinal)
            .ToArray();
        _expertise[agentId] = normalised;
        _expertiseInitialised[agentId] = true;
        return Task.FromResult(normalised);
    }

    public Task<bool> HasExpertiseSetAsync(Guid agentId, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_expertiseInitialised.TryGetValue(agentId, out var v) && v);
    }

    /// <summary>
    /// Test helper: marks <paramref name="agentId"/> as having had its
    /// expertise list explicitly initialised, mirroring the
    /// <c>agent_live_config.expertise_initialised</c> flag.
    /// </summary>
    public void SetExpertiseInitialised(Guid agentId, bool value = true)
    {
        _expertiseInitialised[agentId] = value;
    }

    /// <summary>
    /// Test helper: pre-seeds the metadata for <paramref name="agentId"/>
    /// without going through the partial-PATCH semantics, so tests can
    /// arrange an "agent already configured" baseline.
    /// </summary>
    public void SeedMetadata(Guid agentId, AgentMetadata metadata)
    {
        _metadata[agentId] = metadata;
    }
}
