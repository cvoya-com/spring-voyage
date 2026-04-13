// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Messaging;

using global::Dapr.Actors;

/// <summary>
/// Dapr actor interface for agent actors.
/// Extends <see cref="IActor"/> and mirrors the <see cref="IMessageReceiver"/> contract
/// for use within the Dapr actor runtime.
/// </summary>
public interface IAgentActor : IActor
{
    /// <summary>
    /// Receives and processes a message, optionally returning a response.
    /// </summary>
    /// <param name="message">The message to process.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An optional response message, or <c>null</c> if no response is needed.</returns>
    Task<Message?> ReceiveAsync(Message message, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the agent's currently persisted metadata. Unset fields are
    /// returned as <c>null</c>; callers that need defaults (e.g., <c>Enabled</c>
    /// defaulting to <c>true</c>) apply them at the API layer.
    /// </summary>
    Task<AgentMetadata> GetMetadataAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the agent's metadata with partial PATCH semantics: only
    /// non-<c>null</c> fields on <paramref name="metadata"/> are written; a
    /// <c>null</c> field leaves the existing state untouched. Emits a
    /// <c>StateChanged</c> activity event describing which fields changed.
    /// </summary>
    Task SetMetadataAsync(AgentMetadata metadata, CancellationToken cancellationToken = default);

    /// <summary>
    /// Clears the agent's parent-unit pointer. Called from the unit's
    /// unassign endpoint so that clearing containment is a single operation
    /// at the actor boundary — the partial-PATCH semantics of
    /// <see cref="SetMetadataAsync"/> treat <c>null</c> as "leave untouched,"
    /// which is correct for normal edits but wrong for explicit clearing.
    /// </summary>
    Task ClearParentUnitAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the agent's configured skill list (tool names the agent is
    /// allowed to invoke). An empty list is a legitimate configured state
    /// — the agent is explicitly disabled from every tool. A never-set
    /// agent also returns an empty list; callers that need to distinguish
    /// "never configured" from "configured to nothing" must track it
    /// elsewhere.
    /// </summary>
    Task<IReadOnlyList<string>> GetSkillsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the agent's skill list in full. Callers pass the new
    /// complete list; there are no merge semantics. Duplicates are
    /// collapsed; ordering is not preserved. Emits a <c>StateChanged</c>
    /// activity event describing the change.
    /// </summary>
    Task SetSkillsAsync(IReadOnlyList<string> skills, CancellationToken cancellationToken = default);
}