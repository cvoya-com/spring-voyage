// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Collections.Concurrent;

/// <summary>
/// Tracks running persistent agent services so the dispatcher can reuse them
/// across invocations instead of starting a new container per dispatch.
/// </summary>
/// <remarks>
/// This PR focuses on the ephemeral path. The persistent registry is stubbed
/// out with an in-memory dictionary; a durable implementation backed by Dapr
/// state store will follow when persistent hosting ships (#334).
/// </remarks>
public class PersistentAgentRegistry
{
    private readonly ConcurrentDictionary<string, PersistentAgentEntry> _entries = new();

    /// <summary>
    /// Registers or updates a persistent agent service.
    /// </summary>
    /// <param name="agentId">The agent identifier.</param>
    /// <param name="endpoint">The A2A endpoint URL of the running agent service.</param>
    /// <param name="containerId">The container identifier, if applicable.</param>
    public void Register(string agentId, Uri endpoint, string? containerId = null)
    {
        _entries[agentId] = new PersistentAgentEntry(agentId, endpoint, containerId, DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Attempts to retrieve a running persistent agent entry.
    /// </summary>
    /// <param name="agentId">The agent identifier.</param>
    /// <param name="entry">The entry, if found.</param>
    /// <returns><c>true</c> if the agent is registered.</returns>
    public bool TryGet(string agentId, out PersistentAgentEntry? entry)
    {
        return _entries.TryGetValue(agentId, out entry);
    }

    /// <summary>
    /// Removes a persistent agent entry (e.g. after its container was stopped).
    /// </summary>
    /// <param name="agentId">The agent identifier.</param>
    public void Remove(string agentId)
    {
        _entries.TryRemove(agentId, out _);
    }
}

/// <summary>
/// Describes a running persistent agent service.
/// </summary>
/// <param name="AgentId">The agent identifier.</param>
/// <param name="Endpoint">The A2A endpoint URL the agent is reachable at.</param>
/// <param name="ContainerId">The container identifier, if the agent runs in a container.</param>
/// <param name="StartedAt">When the agent service was started.</param>
public record PersistentAgentEntry(
    string AgentId,
    Uri Endpoint,
    string? ContainerId,
    DateTimeOffset StartedAt);