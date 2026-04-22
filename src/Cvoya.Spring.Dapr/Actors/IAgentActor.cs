// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Dapr actor interface for agent actors. Extends the shared
/// <see cref="IAgent"/> contract (mailbox / message dispatch) with the
/// agent-only surface: metadata, parent-unit pointer, and configured skill
/// list. A unit is also an <see cref="IAgent"/> via <see cref="IUnitActor"/>
/// — use <see cref="IAgent"/> where only the mailbox is needed, and this
/// interface only where the agent-only methods are required.
/// </summary>
public interface IAgentActor : IAgent
{
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
    /// allowed to invoke). An empty array is a legitimate configured state
    /// — the agent is explicitly disabled from every tool. A never-set
    /// agent also returns an empty array; callers that need to distinguish
    /// "never configured" from "configured to nothing" must track it
    /// elsewhere.
    /// </summary>
    /// <remarks>
    /// Bug #319: returns a concrete array so the value crosses the Dapr
    /// actor remoting boundary without a <c>DataContractSerializer</c>
    /// "type not expected" failure on runtime wrapper collections.
    /// </remarks>
    Task<string[]> GetSkillsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the agent's skill list in full. Callers pass the new
    /// complete list; there are no merge semantics. Duplicates are
    /// collapsed; ordering is not preserved. Emits a <c>StateChanged</c>
    /// activity event describing the change.
    /// </summary>
    /// <remarks>
    /// Bug #319: takes a concrete array to keep the full actor surface on
    /// data-contract-safe types (arrays serialize natively).
    /// </remarks>
    Task SetSkillsAsync(string[] skills, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the agent's configured expertise domains. Seeded from the
    /// agent definition (<c>expertise</c> block in YAML) and editable at
    /// runtime through <see cref="SetExpertiseAsync(ExpertiseDomain[], CancellationToken)"/>.
    /// Returned as an array so the value crosses the Dapr remoting boundary
    /// (#319).
    /// </summary>
    /// <remarks>
    /// The aggregator (#412) reads agent expertise through
    /// <see cref="Core.Capabilities.IExpertiseStore"/>, which delegates to
    /// this method — so changing an agent's expertise automatically reshapes
    /// the effective expertise of every ancestor unit once the store
    /// notifies the aggregator via
    /// <c>IExpertiseAggregator.InvalidateAsync</c>.
    /// </remarks>
    Task<ExpertiseDomain[]> GetExpertiseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the agent's expertise in full. Passing an empty array
    /// clears the configuration. Emits a <c>StateChanged</c> activity event
    /// so the observability pipeline (#44) sees directory-shape changes.
    /// </summary>
    Task SetExpertiseAsync(ExpertiseDomain[] domains, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the supplied conversation (#1038). Idempotent — a conversation
    /// id that matches neither the active channel nor any pending channel
    /// returns silently. When the id matches the currently active
    /// conversation the actor cancels in-flight dispatch, removes the
    /// active state, emits a <c>ConversationClosed</c> activity event, and
    /// promotes the next pending conversation onto the active slot. When
    /// the id matches a pending channel that channel is dropped and the
    /// active slot is left untouched.
    /// </summary>
    /// <param name="conversationId">The conversation identifier to close.</param>
    /// <param name="reason">
    /// Optional human-readable reason — surfaced on the
    /// <c>ConversationClosed</c> activity event's <c>details</c> payload so
    /// operators can see <em>why</em> the close happened (operator request,
    /// non-zero dispatch exit, etc.).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task CloseConversationAsync(
        string conversationId,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Off-turn helper that the actor's own dispatch task self-invokes
    /// (via Dapr remoting) when a dispatch terminates abnormally — either a
    /// non-zero container exit (#1036) or an exception in the dispatcher
    /// itself. Mutates persistent actor state — removes
    /// <c>StateKeys.ActiveConversation</c>, emits a <c>StateChanged</c>
    /// (Active → Idle) event, and promotes the next pending conversation —
    /// so it must run on an actor turn. Surfaced on <see cref="IAgentActor"/>
    /// (rather than left as an internal helper) precisely so the off-turn
    /// dispatch task can call it through the actor proxy.
    /// </summary>
    /// <param name="reason">Human-readable reason for clearing the active slot.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task ClearActiveConversationAsync(
        string? reason,
        CancellationToken cancellationToken = default);
}