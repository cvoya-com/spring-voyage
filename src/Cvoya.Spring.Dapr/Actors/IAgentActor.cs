// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Lifecycle;

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
    /// Off-turn helper that the actor's own dispatch task self-invokes
    /// (via Dapr remoting) when a per-thread dispatch terminates
    /// (success, cancel, exception, or non-zero container exit). Mutates
    /// persistent actor state on the per-thread channel — drains any
    /// messages appended for the thread while the dispatch was running
    /// (per-thread FIFO is preserved), or marks the channel idle when
    /// the queue is empty — so it must run on an actor turn. Surfaced on
    /// <see cref="IAgentActor"/> (rather than left as an internal helper)
    /// precisely so the off-turn dispatch task can call it through the
    /// actor proxy. Per-ADR-0030 §44: only this thread's channel is
    /// affected; other threads on the same agent run independently.
    /// </summary>
    /// <param name="threadId">The thread whose dispatcher just exited.</param>
    /// <param name="reason">Human-readable reason for the exit.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task OnDispatchExitAsync(
        string threadId,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a coarse runtime-status snapshot of this agent — the
    /// per-thread channel population the portal renders next to every
    /// agent name (#2100). Distinct from <see cref="GetMetadataAsync"/>
    /// (which returns durable configuration) and from <c>StatusQuery</c>
    /// via the message router (which is the orchestration-tool surface):
    /// this method is a cheap actor-state read intended to be polled at
    /// sub-2s cadence and does not emit a <c>StatusQuery</c> activity event.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A snapshot of the agent's per-thread channel state.</returns>
    Task<AgentRuntimeStatusReport> GetRuntimeStatusAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the agent's installation-lifecycle status (#2156). Distinct
    /// from <see cref="AgentRuntimeStatus"/>: that one is the moment-to-
    /// moment mailbox snapshot; this one records whether the package /
    /// direct-create activation succeeded. Agents whose lifecycle was
    /// never written default to <see cref="LifecycleStatus.Running"/>
    /// — agents installed before #2156 landed completed activation
    /// successfully in the legacy path, so the default is the correct
    /// backwards-compatible answer.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The persisted lifecycle status, or <c>Active</c> when unset.</returns>
    Task<LifecycleStatus> GetLifecycleStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the diagnostic error message persisted alongside an
    /// <see cref="LifecycleStatus.Error"/> status (#2156). <c>null</c>
    /// when the agent is in <see cref="LifecycleStatus.Running"/> or
    /// when an error was recorded without an accompanying message.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<string?> GetLifecycleErrorAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the agent's installation-lifecycle outcome (#2156). Called
    /// by <c>DefaultPackageArtefactActivator</c> immediately after a
    /// successful directory registration (with <see cref="LifecycleStatus.Running"/>
    /// and a <c>null</c> error) and from every <c>catch</c> in the
    /// activator (with <see cref="LifecycleStatus.Error"/> and the
    /// exception message) before the activator rethrows.
    /// </summary>
    /// <param name="status">The lifecycle status to persist.</param>
    /// <param name="error">
    /// Optional human-readable diagnostic. Persisted alongside <paramref name="status"/>
    /// so the GET endpoint can surface "why" without forcing the operator
    /// to comb the worker logs. Pass <c>null</c> when no diagnostic
    /// applies (the parameter has no default — Dapr's actor proxy
    /// generator rejects any non-cancellation-token optional parameter
    /// on a remoted interface, see #2199).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task SetLifecycleStatusAsync(
        LifecycleStatus status,
        string? error,
        CancellationToken cancellationToken = default);
}
