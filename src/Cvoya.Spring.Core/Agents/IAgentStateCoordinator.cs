// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Agents;

using Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Seam that encapsulates the persisted-config CRUD concern extracted
/// from <c>AgentActor</c>: reading and writing the agent's metadata,
/// skills, and expertise domains, and emitting the corresponding
/// <see cref="ActivityEventType.StateChanged"/> activity events.
/// </summary>
/// <remarks>
/// <para>
/// The interface lives in <c>Cvoya.Spring.Core</c> so the cloud host
/// can substitute a tenant-aware coordinator (e.g. one that layers
/// audit logging on every metadata write, or gates skill assignment on
/// per-tenant allowlists) without touching the actor. Per the
/// platform's "interface-first + TryAdd*" rule, production DI registers
/// the default implementation with <c>TryAddSingleton</c> so the
/// private repo's registration takes precedence when present.
/// </para>
/// <para>
/// Per ADR-0040 / #2048 every method is now backed by EF
/// (<c>agent_live_config</c>, <c>agent_tool_grants</c>,
/// <c>agent_expertise</c>). The actor passes its actor id; the
/// coordinator owns the EF read/write through an injected store. There
/// are no actor-state delegates because there is no actor-state copy
/// to keep in sync.
/// </para>
/// <para>
/// The coordinator is stateless with respect to any individual agent —
/// the per-agent identifier is a parameter on every method. This makes
/// it safe to register as a singleton and share across all
/// <c>AgentActor</c> instances.
/// </para>
/// </remarks>
public interface IAgentStateCoordinator
{
    /// <summary>
    /// Reads the agent's persisted metadata from EF and returns it as
    /// an <see cref="AgentMetadata"/> record.
    /// <see cref="AgentMetadata.ParentUnit"/> is always <c>null</c> on
    /// the returned record — the membership table is the authoritative
    /// parent-unit pointer (ADR-0040). API surfaces that need the
    /// derived parent slug compose <c>GetMetadataAsync</c> output with
    /// a membership lookup.
    /// </summary>
    /// <param name="agentId">The Dapr actor id of the agent.</param>
    /// <param name="cancellationToken">Cancels the read operation.</param>
    Task<AgentMetadata> GetMetadataAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes whichever fields of <paramref name="metadata"/> are
    /// non-<c>null</c> to the agent's <c>agent_live_config</c> row and
    /// emits a <see cref="ActivityEventType.StateChanged"/> event. Does
    /// nothing (and emits no event) when every applicable field is
    /// <c>null</c>. <see cref="AgentMetadata.ParentUnit"/> is silently
    /// ignored — membership writes go through the assign / unassign
    /// endpoints (ADR-0040).
    /// </summary>
    /// <param name="agentId">The Dapr actor id of the agent.</param>
    /// <param name="metadata">The partial patch to apply.</param>
    /// <param name="emitActivity">
    /// Delegate that publishes an <see cref="ActivityEvent"/> to the
    /// activity bus. Called once with a
    /// <see cref="ActivityEventType.StateChanged"/> event after all
    /// non-null fields have been persisted.
    /// </param>
    /// <param name="cancellationToken">Cancels the write operation.</param>
    Task SetMetadataAsync(
        string agentId,
        AgentMetadata metadata,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the agent's configured expertise domains from EF, sorted
    /// by domain name. Returns an empty array when no rows exist.
    /// </summary>
    /// <param name="agentId">The Dapr actor id of the agent.</param>
    /// <param name="cancellationToken">Cancels the read operation.</param>
    Task<ExpertiseDomain[]> GetExpertiseAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the agent's expertise list in full. Empty list is a
    /// legitimate "no expertise" state and still flips the agent's
    /// <c>expertise_initialised</c> flag so the activation seeder
    /// honours the actor-state-wins precedence rule (#488). Emits a
    /// <see cref="ActivityEventType.StateChanged"/> event after the
    /// write commits.
    /// </summary>
    /// <param name="agentId">The Dapr actor id of the agent.</param>
    /// <param name="domains">The expertise domains to normalise and persist.</param>
    /// <param name="emitActivity">
    /// Delegate that publishes an <see cref="ActivityEvent"/> to the
    /// activity bus.
    /// </param>
    /// <param name="cancellationToken">Cancels the write operation.</param>
    Task SetExpertiseAsync(
        string agentId,
        ExpertiseDomain[] domains,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns <c>true</c> when an expertise list has been written for
    /// <paramref name="agentId"/> at all (even an empty list). Used by
    /// the activation seeder so an explicit operator edit (including a
    /// deliberate clear) is preserved across process restart even when
    /// the persisted list happens to be empty.
    /// </summary>
    Task<bool> HasExpertiseSetAsync(string agentId, CancellationToken cancellationToken = default);
}
