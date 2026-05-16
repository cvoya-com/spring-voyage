// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Lifecycle;

/// <summary>
/// Dapr actor interface for agent actors. Extends the shared
/// <see cref="IAgent"/> contract (mailbox / message dispatch) with the
/// agent-only surface: metadata, parent-unit pointer, configured skill list,
/// and (since #2364) the same lifecycle state machine the unit actor
/// implements — <see cref="LifecycleStatus.Draft"/> →
/// <see cref="LifecycleStatus.Validating"/> →
/// <see cref="LifecycleStatus.Stopped"/> → <see cref="LifecycleStatus.Running"/>
/// driven by the shared <c>ArtefactValidationWorkflow</c>. A unit is also an
/// <see cref="IAgent"/> via <see cref="IUnitActor"/> — use <see cref="IAgent"/>
/// where only the mailbox is needed, and this interface only where the
/// agent-only methods are required.
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
    /// the queue is empty — so it must run on an actor turn. Per ADR-0030 §44:
    /// only this thread's channel is affected; other threads on the same
    /// agent run independently.
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
    /// (which returns durable configuration), from <see cref="GetStatusAsync"/>
    /// (which returns the installation lifecycle state), and from
    /// <c>StatusQuery</c> via the message router (which is the
    /// orchestration-tool surface): this method is a cheap actor-state read
    /// intended to be polled at sub-2s cadence and does not emit a
    /// <c>StatusQuery</c> activity event.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A snapshot of the agent's per-thread channel state.</returns>
    Task<AgentRuntimeStatusReport> GetRuntimeStatusAsync(
        CancellationToken cancellationToken = default);

    // ──────────────────────────────────────────────────────────────────────
    // Lifecycle state machine (#2364) — mirrors IUnitActor.
    // Agents and units share the same Draft → Validating → Stopped →
    // Starting → Running progression driven by the shared
    // ArtefactValidationWorkflow. Agents do NOT carry connector bindings in
    // v0.1, so the agent-side TryAutoStartAsync skips the dispatcher hop
    // that UnitActor performs between Starting and Running.
    // ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Gets the persisted lifecycle status of this agent. An agent that has
    /// never transitioned reports <see cref="LifecycleStatus.Draft"/>.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The current lifecycle status.</returns>
    Task<LifecycleStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the diagnostic error message persisted alongside an
    /// <see cref="LifecycleStatus.Error"/> status. <c>null</c> when the
    /// agent is not in <c>Error</c>, or when an error was recorded without
    /// an accompanying message.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<string?> GetLifecycleErrorAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts a lifecycle transition to <paramref name="target"/>. If the
    /// transition is not permitted from the current status (per
    /// <see cref="LifecycleTransitions.IsValidTransition"/>), the status is
    /// left unchanged and a rejection reason is returned. On a successful
    /// transition into <see cref="LifecycleStatus.Validating"/>, the actor
    /// also schedules a fresh <c>ArtefactValidationWorkflow</c> run via the
    /// shared <c>IArtefactValidationCoordinator</c>.
    /// </summary>
    /// <param name="target">The target status.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<TransitionResult> TransitionAsync(
        LifecycleStatus target,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Terminal callback the <c>ArtefactValidationWorkflow</c> invokes when
    /// its probe run finishes. Drives the <see cref="LifecycleStatus.Validating"/>
    /// → <see cref="LifecycleStatus.Stopped"/> or
    /// <see cref="LifecycleStatus.Validating"/> → <see cref="LifecycleStatus.Error"/>
    /// transition and persists the redacted failure payload on failure. On
    /// successful completion the actor also consumes the
    /// <see cref="SetPendingAutoStartAsync"/> marker if set and drives the
    /// <c>Stopped → Starting → Running</c> tail.
    /// </summary>
    /// <param name="completion">The workflow's terminal outcome.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<TransitionResult> CompleteValidationAsync(
        ArtefactValidationCompletion completion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks this agent as awaiting an automatic transition into
    /// <see cref="LifecycleStatus.Running"/> once
    /// <see cref="CompleteValidationAsync"/> reports a successful validation
    /// outcome (#2364). Called by the activator after the agent transitions
    /// to <see cref="LifecycleStatus.Validating"/> so a freshly installed
    /// agent ends up usable without a manual <c>POST /agents/{id}/start</c>.
    /// </summary>
    /// <remarks>
    /// The flag is consumed and cleared inside
    /// <see cref="CompleteValidationAsync"/>; setting it twice before
    /// validation finishes is idempotent. Setting it after a validation has
    /// already completed has no effect on the already-applied transition —
    /// the agent stays in <see cref="LifecycleStatus.Stopped"/>.
    /// </remarks>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task SetPendingAutoStartAsync(CancellationToken cancellationToken = default);
}
