// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default <see cref="IRuntimeInvocationPath"/> implementation. Owns the
/// resolve-config → resolve-skills → resolve-tools → resolve-credentials →
/// launch → publish pipeline that used to live inline on
/// <c>AgentActor.HandleDomainMessageAsync</c>'s activate-and-dispatch
/// closure.
/// </summary>
/// <remarks>
/// <para>
/// The class is registered as a singleton: it is stateless across
/// subjects and captures no Dapr actor types. All subject-specific state
/// (definition, skills, platform messaging and directory tools) resolves through injected
/// seams keyed on the subject's address.
/// </para>
/// <para>
/// Two call shapes are provided so the same pipeline serves both halves
/// of the ADR-0039 transition:
/// </para>
/// <list type="bullet">
///   <item>
///     <description>
///     <see cref="InvokeAsync(Address, Message, CancellationToken)"/> —
///     the lean public surface declared by
///     <see cref="IRuntimeInvocationPath"/>. Builds a minimal
///     <see cref="PromptAssemblyContext"/> from the
///     <see cref="IAgentDefinitionProvider"/> and the
///     <see cref="ISkillRegistry"/> enumeration alone.
///     This is the surface task C2 wires <c>UnitActor</c> through —
///     units have no per-mailbox prior-message buffer, so the lean shape
///     is sufficient for them.
///     </description>
///   </item>
///   <item>
///     <description>
///     <see cref="InvokeAsync(Address, Message, PromptAssemblyContext, Func{ActivityEvent, CancellationToken, Task}, Func{string, Task}, CancellationToken)"/> —
///     the rich overload that <c>AgentActor</c> calls today so its
///     mailbox-aware context (prior thread messages, pending
///     amendments, effective per-membership metadata) and its
///     activity-emission / active-thread-clearing delegates flow
///     through unchanged. This preserves behaviour exactly for
///     C1 (no functional change for agents).
///     </description>
///   </item>
/// </list>
/// <para>
/// Both call shapes funnel into <see cref="IAgentDispatchCoordinator"/>,
/// which owns the actual execution-dispatcher invocation, exit-code
/// inspection, response-routing, and abnormal-termination clean-up.
/// </para>
/// </remarks>
public class RuntimeInvocationPath(
    IAgentDefinitionProvider agentDefinitionProvider,
    IEnumerable<ISkillRegistry> skillRegistries,
    IAgentDispatchCoordinator dispatchCoordinator) : IRuntimeInvocationPath
{
    private readonly IReadOnlyList<ISkillRegistry> _skillRegistries = skillRegistries.ToList();

    /// <inheritdoc />
    public async Task InvokeAsync(
        Address subject,
        Message inbound,
        CancellationToken ct,
        Func<ActivityEvent, CancellationToken, Task>? emitActivity = null)
    {
        var context = await BuildContextAsync(subject, inbound, ct);

        // The lean path is used by UnitActor, whose runtime invocation can
        // outlive the HTTP request that originally delivered the message.
        // Once the message has reached the unit actor and its prompt context
        // is built, client disconnects must not cancel the long-running A2A
        // dispatch; otherwise the agent may finish but the platform stops
        // polling before it can route the response back to the human thread.
        //
        // When the caller supplies an activity-emission delegate (e.g. the
        // unit actor's own EmitActivityEventAsync), adapt coordinator
        // events back to the real subject address so unit dispatch errors
        // are visible in unit-scoped Activity streams. The lean dispatch
        // exit handler also turns "no response" completions into a neutral
        // activity row so operators can distinguish a completed silent
        // dispatch from one still in flight (#2207). When null, fall back
        // to no-ops so callers without an activity channel still see the
        // original lean-overload behaviour (#2211).
        var leanEmitActivity = CreateLeanActivityEmitter(subject, emitActivity);
        var leanDispatchExit = CreateLeanDispatchExitHandler(subject, inbound, emitActivity);

        await dispatchCoordinator.RunDispatchAsync(
            agentId: subject.Path,
            message: inbound,
            context: context,
            emitActivity: leanEmitActivity,
            onDispatchExit: leanDispatchExit,
            cancellationToken: CancellationToken.None);
    }

    /// <summary>
    /// Rich overload used by <c>AgentActor</c> while the actor still owns
    /// per-mailbox prior-message buffering and per-actor activity
    /// emission. The pre-built <paramref name="context"/> is forwarded
    /// to <see cref="IAgentDispatchCoordinator.RunDispatchAsync"/>
    /// unchanged so behaviour is identical to the inline pipeline this
    /// helper replaces.
    /// </summary>
    /// <param name="subject">
    /// The address of the agent whose runtime is being invoked. Used to
    /// derive the agent id passed to
    /// <see cref="IAgentDispatchCoordinator.RunDispatchAsync"/> for log
    /// correlation.
    /// </param>
    /// <param name="inbound">
    /// The message that triggered the dispatch. Carries the thread id
    /// being served.
    /// </param>
    /// <param name="context">
    /// The prompt-assembly context the caller has already built. The
    /// dispatch coordinator forwards this verbatim to
    /// <see cref="IExecutionDispatcher.DispatchAsync"/>.
    /// </param>
    /// <param name="emitActivity">
    /// Per-actor activity-emission delegate. Forwarded to the dispatch
    /// coordinator so error events surface through the actor's
    /// publishing pipeline.
    /// </param>
    /// <param name="onDispatchExit">
    /// Per-actor per-thread dispatch-exit delegate. Forwarded so the
    /// actor's mailbox can drain remaining queued messages on the thread
    /// or mark the channel idle when the dispatcher returns
    /// (#2076 / ADR-0030 §3 §44).
    /// </param>
    /// <param name="ct">A token to cancel the pipeline.</param>
    public Task InvokeAsync(
        Address subject,
        Message inbound,
        PromptAssemblyContext context,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        Func<string, Task> onDispatchExit,
        CancellationToken ct)
    {
        return dispatchCoordinator.RunDispatchAsync(
            agentId: subject.Path,
            message: inbound,
            context: context,
            emitActivity: emitActivity,
            onDispatchExit: onDispatchExit,
            cancellationToken: ct);
    }

    /// <summary>
    /// Builds a minimal <see cref="PromptAssemblyContext"/> for the lean
    /// <see cref="InvokeAsync(Address, Message, CancellationToken)"/>
    /// path: agent instructions from <see cref="IAgentDefinitionProvider"/>
    /// and skills from the registered <see cref="ISkillRegistry"/> set —
    /// messaging tools (<c>sv.messaging.*</c>) ride the same registry set
    /// since ADR-0051. Per-mailbox prior
    /// messages, pending amendments, and effective per-membership
    /// metadata are intentionally absent — those are caller-side
    /// concerns the rich overload is designed to carry.
    /// </summary>
    private async Task<PromptAssemblyContext> BuildContextAsync(
        Address subject, Message inbound, CancellationToken ct)
    {
        var definition = await agentDefinitionProvider.GetByIdAsync(subject.Path, ct);

        var skills = _skillRegistries
            .Select(r => new Skill(
                Name: r.Name,
                Description: $"Tools exposed by the {r.Name} connector.",
                Tools: r.GetToolDefinitions()))
            .ToList();

        // ADR-0051: sv.messaging.send / sv.messaging.multicast are served by
        // the single platform MCP server as SvMessagingSkillRegistry — they
        // surface through the ISkillRegistry enumeration above like every
        // other sv.* tool, so no separate messaging-tool resolution is needed.
        _ = inbound;

        return new PromptAssemblyContext(
            Policies: null,
            Skills: skills,
            PriorMessages: Array.Empty<Message>(),
            LastCheckpoint: null,
            AgentInstructions: definition?.Instructions,
            EffectiveMetadata: null,
            PendingAmendments: null);
    }

    private static Func<ActivityEvent, CancellationToken, Task> CreateLeanActivityEmitter(
        Address subject,
        Func<ActivityEvent, CancellationToken, Task>? emitActivity)
    {
        if (emitActivity is null)
        {
            return (_, _) => Task.CompletedTask;
        }

        return (activityEvent, cancellationToken) =>
            emitActivity(activityEvent with { Source = subject }, cancellationToken);
    }

    private static Func<string, Task> CreateLeanDispatchExitHandler(
        Address subject,
        Message inbound,
        Func<ActivityEvent, CancellationToken, Task>? emitActivity)
    {
        if (emitActivity is null)
        {
            return _ => Task.CompletedTask;
        }

        return reason =>
        {
            if (!string.Equals(reason, AgentDispatchCoordinator.DispatchNoResponseReason, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            var details = JsonSerializer.SerializeToElement(new
            {
                reason,
                subject = subject.ToString(),
                threadId = inbound.ThreadId,
            });

            return emitActivity(
                new ActivityEvent(
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow,
                    subject,
                    ActivityEventType.WorkflowStepCompleted,
                    ActivitySeverity.Info,
                    "Dispatch returned no response.",
                    details,
                    inbound.ThreadId),
                CancellationToken.None);
        };
    }
}
