// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Agents;

using Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Seam that encapsulates the persisted-config CRUD concern extracted from
/// <c>AgentActor</c>: reading and writing the agent's metadata, skills, and
/// expertise domains, and emitting the corresponding
/// <see cref="ActivityEventType.StateChanged"/> activity events.
/// </summary>
/// <remarks>
/// <para>
/// The interface lives in <c>Cvoya.Spring.Core</c> so the cloud host can
/// substitute a tenant-aware coordinator (e.g. one that layers audit logging
/// on every metadata write, or gates skill assignment on per-tenant allowlists)
/// without touching the actor. Per the platform's "interface-first + TryAdd*"
/// rule, production DI registers the default implementation with
/// <c>TryAddSingleton</c> so the private repo's registration takes precedence
/// when present.
/// </para>
/// <para>
/// The coordinator holds zero Dapr-actor references. Every method receives
/// delegate parameters so the actor injects its own state-read, state-write,
/// and activity-emission implementations without the coordinator depending on
/// Dapr actor types or scoped DI services. The metadata methods are pure CRUD
/// over actor state; no scoped services are required.
/// </para>
/// <para>
/// The coordinator is stateless with respect to any individual agent — it
/// operates entirely through the per-call delegates. This makes it safe to
/// register as a singleton and share across all <c>AgentActor</c> instances.
/// </para>
/// </remarks>
public interface IAgentStateCoordinator
{
    /// <summary>
    /// Reads the agent's persisted metadata from actor state and returns it
    /// as an <see cref="AgentMetadata"/> record.
    /// </summary>
    /// <param name="agentId">The Dapr actor id of the agent. Used for log correlation.</param>
    /// <param name="getModel">
    /// Delegate that reads the agent's model identifier from actor state.
    /// Returns a <c>(hasValue, value)</c> pair; <c>hasValue</c> is <c>false</c>
    /// when the key has never been set.
    /// </param>
    /// <param name="getSpecialty">
    /// Delegate that reads the agent's specialty label from actor state.
    /// Returns a <c>(hasValue, value)</c> pair.
    /// </param>
    /// <param name="getEnabled">
    /// Delegate that reads the agent's enabled flag from actor state.
    /// Returns a <c>(hasValue, value)</c> pair.
    /// </param>
    /// <param name="getExecutionMode">
    /// Delegate that reads the agent's execution mode from actor state.
    /// Returns a <c>(hasValue, value)</c> pair.
    /// </param>
    /// <param name="getParentUnit">
    /// Delegate that reads the agent's parent-unit pointer from actor state.
    /// Returns a <c>(hasValue, value)</c> pair.
    /// </param>
    /// <param name="cancellationToken">Cancels the read operation.</param>
    /// <returns>
    /// An <see cref="AgentMetadata"/> record whose fields are <c>null</c>
    /// for any key that has not been set in actor state.
    /// </returns>
    Task<AgentMetadata> GetMetadataAsync(
        string agentId,
        Func<CancellationToken, Task<(bool hasValue, string? value)>> getModel,
        Func<CancellationToken, Task<(bool hasValue, string? value)>> getSpecialty,
        Func<CancellationToken, Task<(bool hasValue, bool value)>> getEnabled,
        Func<CancellationToken, Task<(bool hasValue, AgentExecutionMode value)>> getExecutionMode,
        Func<CancellationToken, Task<(bool hasValue, string? value)>> getParentUnit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes whichever fields of <paramref name="metadata"/> are non-<c>null</c>
    /// to actor state and emits a <see cref="ActivityEventType.StateChanged"/>
    /// event. Does nothing (and emits no event) when every field is <c>null</c>.
    /// </summary>
    /// <param name="agentId">The Dapr actor id of the agent. Used for log correlation.</param>
    /// <param name="metadata">
    /// The partial patch to apply. Null fields mean "leave the current value alone."
    /// </param>
    /// <param name="setModel">
    /// Delegate that writes the model identifier to actor state.
    /// </param>
    /// <param name="setSpecialty">
    /// Delegate that writes the specialty label to actor state.
    /// </param>
    /// <param name="setEnabled">
    /// Delegate that writes the enabled flag to actor state.
    /// </param>
    /// <param name="setExecutionMode">
    /// Delegate that writes the execution mode to actor state.
    /// </param>
    /// <param name="setParentUnit">
    /// Delegate that writes the parent-unit pointer to actor state.
    /// </param>
    /// <param name="emitActivity">
    /// Delegate that publishes an <see cref="ActivityEvent"/> to the activity
    /// bus. Called once with a <see cref="ActivityEventType.StateChanged"/> event
    /// after all non-null fields have been persisted.
    /// </param>
    /// <param name="cancellationToken">Cancels the write operation.</param>
    Task SetMetadataAsync(
        string agentId,
        AgentMetadata metadata,
        Func<string, CancellationToken, Task> setModel,
        Func<string, CancellationToken, Task> setSpecialty,
        Func<bool, CancellationToken, Task> setEnabled,
        Func<AgentExecutionMode, CancellationToken, Task> setExecutionMode,
        Func<string, CancellationToken, Task> setParentUnit,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes the agent's parent-unit pointer from actor state and emits a
    /// <see cref="ActivityEventType.StateChanged"/> event. Used by the unit's
    /// unassign endpoint alongside removal from the unit's member list so that
    /// <see cref="AgentMetadata.ParentUnit"/> and the unit member list stay in
    /// sync. Separated from <see cref="SetMetadataAsync"/> because the
    /// partial-patch semantics there treat <c>null</c> as "leave untouched."
    /// </summary>
    /// <param name="agentId">The Dapr actor id of the agent. Used for log correlation.</param>
    /// <param name="removeParentUnit">
    /// Delegate that removes the parent-unit state key from actor state.
    /// </param>
    /// <param name="emitActivity">
    /// Delegate that publishes an <see cref="ActivityEvent"/> to the activity
    /// bus. Called once with a <see cref="ActivityEventType.StateChanged"/> event
    /// after the state key is removed.
    /// </param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    Task ClearParentUnitAsync(
        string agentId,
        Func<CancellationToken, Task> removeParentUnit,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the agent's configured skill list from actor state.
    /// </summary>
    /// <param name="agentId">The Dapr actor id of the agent. Used for log correlation.</param>
    /// <param name="getSkills">
    /// Delegate that reads the skill list from actor state. Returns a
    /// <c>(hasValue, value)</c> pair; <c>hasValue</c> is <c>false</c> when the
    /// key has never been set.
    /// </param>
    /// <param name="cancellationToken">Cancels the read operation.</param>
    /// <returns>
    /// The stored skill array, or an empty array when no skills have been set.
    /// </returns>
    Task<string[]> GetSkillsAsync(
        string agentId,
        Func<CancellationToken, Task<(bool hasValue, List<string>? value)>> getSkills,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Normalises and writes the agent's skill list to actor state, then emits a
    /// <see cref="ActivityEventType.StateChanged"/> event. Normalisation: drops
    /// null/whitespace entries, trims, deduplicates, and sorts lexicographically
    /// so that diffs in logs and activity events are predictable.
    /// </summary>
    /// <param name="agentId">The Dapr actor id of the agent. Used for log correlation.</param>
    /// <param name="skills">The skill list to normalise and persist.</param>
    /// <param name="setSkills">
    /// Delegate that writes the normalised skill list to actor state.
    /// </param>
    /// <param name="emitActivity">
    /// Delegate that publishes an <see cref="ActivityEvent"/> to the activity
    /// bus. Called once with a <see cref="ActivityEventType.StateChanged"/> event
    /// after the normalised list is persisted.
    /// </param>
    /// <param name="cancellationToken">Cancels the write operation.</param>
    Task SetSkillsAsync(
        string agentId,
        string[] skills,
        Func<List<string>, CancellationToken, Task> setSkills,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the agent's configured expertise domains from actor state.
    /// </summary>
    /// <param name="agentId">The Dapr actor id of the agent. Used for log correlation.</param>
    /// <param name="getExpertise">
    /// Delegate that reads the expertise domain list from actor state. Returns a
    /// <c>(hasValue, value)</c> pair; <c>hasValue</c> is <c>false</c> when the
    /// key has never been set.
    /// </param>
    /// <param name="cancellationToken">Cancels the read operation.</param>
    /// <returns>
    /// The stored expertise array, or an empty array when no expertise has been set.
    /// </returns>
    Task<ExpertiseDomain[]> GetExpertiseAsync(
        string agentId,
        Func<CancellationToken, Task<(bool hasValue, List<ExpertiseDomain>? value)>> getExpertise,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Normalises and writes the agent's expertise domain list to actor state,
    /// then emits a <see cref="ActivityEventType.StateChanged"/> event.
    /// Normalisation: drops entries with null/whitespace names, deduplicates by
    /// name case-insensitively (last write wins so a caller can PATCH a level or
    /// description by re-listing the same domain), and sorts by name.
    /// </summary>
    /// <param name="agentId">The Dapr actor id of the agent. Used for log correlation.</param>
    /// <param name="domains">The expertise domains to normalise and persist.</param>
    /// <param name="setExpertise">
    /// Delegate that writes the normalised expertise list to actor state.
    /// </param>
    /// <param name="emitActivity">
    /// Delegate that publishes an <see cref="ActivityEvent"/> to the activity
    /// bus. Called once with a <see cref="ActivityEventType.StateChanged"/> event
    /// after the normalised list is persisted.
    /// </param>
    /// <param name="cancellationToken">Cancels the write operation.</param>
    Task SetExpertiseAsync(
        string agentId,
        ExpertiseDomain[] domains,
        Func<List<ExpertiseDomain>, CancellationToken, Task> setExpertise,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken = default);
}