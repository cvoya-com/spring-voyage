// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Skills;

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
/// (definition, skills, orchestration tools) resolves through injected
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
///     <see cref="IAgentDefinitionProvider"/>, <see cref="ISkillRegistry"/>
///     enumeration, and <see cref="IOrchestrationToolProvider"/> alone.
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
    IOrchestrationToolProvider orchestrationToolProvider,
    IAgentDispatchCoordinator dispatchCoordinator,
    ILogger<RuntimeInvocationPath> logger) : IRuntimeInvocationPath
{
    private readonly IReadOnlyList<ISkillRegistry> _skillRegistries = skillRegistries.ToList();

    /// <inheritdoc />
    public async Task InvokeAsync(Address subject, Message inbound, CancellationToken ct)
    {
        var context = await BuildContextAsync(subject, inbound, ct);

        await dispatchCoordinator.RunDispatchAsync(
            agentId: subject.Path,
            message: inbound,
            context: context,
            emitActivity: (_, _) => Task.CompletedTask,
            clearActiveThread: _ => Task.CompletedTask,
            cancellationToken: ct);
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
    /// <param name="clearActiveThread">
    /// Per-actor active-thread clear delegate. Forwarded so abnormal
    /// dispatch terminations (cancel, exception, non-zero exit) release
    /// the actor's <c>ActiveThread</c> slot.
    /// </param>
    /// <param name="ct">A token to cancel the pipeline.</param>
    public Task InvokeAsync(
        Address subject,
        Message inbound,
        PromptAssemblyContext context,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        Func<string, Task> clearActiveThread,
        CancellationToken ct)
    {
        return dispatchCoordinator.RunDispatchAsync(
            agentId: subject.Path,
            message: inbound,
            context: context,
            emitActivity: emitActivity,
            clearActiveThread: clearActiveThread,
            cancellationToken: ct);
    }

    /// <summary>
    /// Builds a minimal <see cref="PromptAssemblyContext"/> for the lean
    /// <see cref="InvokeAsync(Address, Message, CancellationToken)"/>
    /// path: agent instructions from <see cref="IAgentDefinitionProvider"/>,
    /// skills from the registered <see cref="ISkillRegistry"/> set, and
    /// orchestration tools from
    /// <see cref="IOrchestrationToolProvider"/>. Per-mailbox prior
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

        // Resolve the orchestration tools the subject may invoke against
        // its child composition. Empty for leaf agents; populated by the
        // directory-driven provider for units with children.
        // The provider takes a Guid threadId — the inbound thread id is
        // a string envelope, but every domain message that reaches here
        // carries a parseable id. Non-parseable values fall back to
        // Guid.Empty so the provider can still return a "no tools"
        // verdict deterministically.
        Guid threadId = Guid.Empty;
        if (!string.IsNullOrWhiteSpace(inbound.ThreadId)
            && !Guid.TryParse(inbound.ThreadId, out threadId))
        {
            logger.LogDebug(
                "Inbound thread id {ThreadId} for subject {Subject} is not a Guid; orchestration-tool resolution uses Guid.Empty.",
                inbound.ThreadId, subject);
        }

        var orchestrationTools = orchestrationToolProvider.GetOrchestrationTools(subject, threadId);
        if (orchestrationTools.Length > 0)
        {
            logger.LogDebug(
                "Resolved {Count} orchestration tool(s) for subject {Subject} on thread {ThreadId}.",
                orchestrationTools.Length, subject, threadId);
        }

        // OrchestrationToolDescriptor is not yet folded into
        // PromptAssemblyContext.Skills (#1786 / D2 wires that). For C1
        // the tools are resolved-but-not-yet-attached; surfacing them in
        // the context is the next phase's deliverable.

        return new PromptAssemblyContext(
            Members: Array.Empty<Address>(),
            Policies: null,
            Skills: skills,
            PriorMessages: Array.Empty<Message>(),
            LastCheckpoint: null,
            AgentInstructions: definition?.Instructions,
            EffectiveMetadata: null,
            PendingAmendments: null);
    }
}