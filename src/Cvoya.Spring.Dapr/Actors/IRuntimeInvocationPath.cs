// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Encapsulates the runtime-invocation pipeline for a single addressable
/// subject (an agent, or — after ADR-0039 task C2 — a unit). The pipeline
/// resolves the subject's configuration, the skills it exposes, the
/// orchestration tools it can invoke against its child composition,
/// the credentials needed by its runtime, launches the runtime via
/// <see cref="Cvoya.Spring.Core.Execution.IAgentRuntimeLauncher"/>, and
/// publishes the response back through the platform message router.
/// </summary>
/// <remarks>
/// <para>
/// Introduced by ADR-0039 ("units are agents"): under that decision the
/// orchestration tool surface is no longer a property of a separate
/// <c>UnitActor</c> type — it is a per-thread capability resolved for a
/// given addressable subject. Phase C of the execution plan extracts the
/// runtime-invocation half of the agent dispatch path into this seam so
/// the two actor types can share it; phase D wires the directory-driven
/// orchestration provider on top.
/// </para>
/// <para>
/// The interface lives in <c>Cvoya.Spring.Dapr.Actors</c> because it is
/// the actor-side composition seam that calls into
/// <c>Cvoya.Spring.Core</c> abstractions
/// (<see cref="Cvoya.Spring.Core.Execution.IAgentDispatchCoordinator"/>,
/// <see cref="Cvoya.Spring.Core.Execution.IExecutionDispatcher"/>);
/// it is therefore Dapr-side glue rather than a pure domain abstraction.
/// </para>
/// <para>
/// Implementations are stateless across subjects — a single singleton
/// services every actor instance. Per-subject and per-thread state flows
/// through the <paramref name="subject"/> address and the
/// <paramref name="inbound"/> message envelope.
/// </para>
/// </remarks>
public interface IRuntimeInvocationPath
{
    /// <summary>
    /// Runs the runtime-invocation pipeline for <paramref name="subject"/>
    /// in response to <paramref name="inbound"/>. Returns once the
    /// pipeline has launched the runtime and arranged for the response to
    /// be published back through the platform message router; the call
    /// does not block on the runtime's reply (the dispatcher is
    /// fire-and-forget).
    /// </summary>
    /// <param name="subject">
    /// The address of the agent or unit whose runtime is being invoked.
    /// Used to resolve subject-scoped configuration (definition, skills,
    /// orchestration tools, credentials).
    /// </param>
    /// <param name="inbound">
    /// The message that triggered this invocation. Carries the thread id
    /// the runtime is serving plus the original sender address used when
    /// publishing the runtime's response.
    /// </param>
    /// <param name="ct">A token to cancel the pipeline.</param>
    /// <param name="emitActivity">
    /// Optional per-caller activity-emission delegate. When non-null, the
    /// pipeline forwards it to
    /// <see cref="IAgentDispatchCoordinator.RunDispatchAsync"/> so error
    /// events (e.g. credential resolution failures) surface through the
    /// caller's activity-publishing pipeline. When null, activity events
    /// emitted by the dispatch coordinator are dropped — preserving the
    /// original lean-overload behaviour for callers that have no activity
    /// channel of their own (#2211).
    /// </param>
    Task InvokeAsync(
        Address subject,
        Message inbound,
        CancellationToken ct,
        Func<ActivityEvent, CancellationToken, Task>? emitActivity = null);

    /// <summary>
    /// Rich overload used while a caller still owns its own per-subject
    /// state (e.g. <c>AgentActor</c>'s mailbox prior-message buffer,
    /// pending amendments, and effective per-membership metadata) and
    /// its own activity-emission / active-thread-clearing delegates.
    /// The pre-built <paramref name="context"/> and the per-call
    /// delegates flow through the underlying dispatch coordinator
    /// unchanged so this overload preserves existing actor behaviour
    /// exactly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Once the directory-driven orchestration provider lands in ADR-0039
    /// task D2 and per-thread state moves behind a shared seam, this
    /// overload will collapse into the lean form above. Until then it
    /// remains the bridge that lets the runtime-invocation pipeline
    /// serve <c>AgentActor</c>'s mailbox-aware path without rewriting
    /// the mailbox layer in the same PR.
    /// </para>
    /// </remarks>
    /// <param name="subject">
    /// The address of the agent or unit whose runtime is being invoked.
    /// </param>
    /// <param name="inbound">
    /// The message that triggered the dispatch.
    /// </param>
    /// <param name="context">
    /// The prompt-assembly context the caller has already built.
    /// </param>
    /// <param name="emitActivity">
    /// Per-caller activity-emission delegate.
    /// </param>
    /// <param name="onDispatchExit">
    /// Per-caller per-thread dispatch-exit delegate. The pipeline forwards
    /// this verbatim to <see cref="IAgentDispatchCoordinator.RunDispatchAsync"/>
    /// so the actor's mailbox can drain remaining queued messages on the
    /// thread or mark the channel idle when the dispatcher returns
    /// (#2076 / ADR-0030 §3 §44).
    /// </param>
    /// <param name="ct">A token to cancel the pipeline.</param>
    Task InvokeAsync(
        Address subject,
        Message inbound,
        PromptAssemblyContext context,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        Func<string, Task> onDispatchExit,
        CancellationToken ct);
}
