// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="IAgentDispatchCoordinator"/>.
/// Owns the execution-dispatch concern extracted from <c>AgentActor</c>:
/// invoking the <see cref="IExecutionDispatcher"/>, observing the runtime's
/// <see cref="RuntimeOutcome"/>, emitting the per-phase activity stream
/// (<see cref="ActivityEventType.MessageDispatchedToRuntime"/>,
/// <see cref="ActivityEventType.RuntimeStarted"/>,
/// <see cref="ActivityEventType.RuntimeReasoning"/>, plus exactly one of
/// <see cref="ActivityEventType.RuntimeCompleted"/> /
/// <see cref="ActivityEventType.RuntimeFailed"/> /
/// <see cref="ActivityEventType.RuntimeCompletedSilent"/> as the terminal),
/// and signalling the per-thread dispatch exit so the actor's mailbox can
/// drain remaining queued messages on the same thread or mark the channel
/// idle.
/// </summary>
/// <remarks>
/// <para>
/// Per <see href="../../../docs/decisions/0056-tool-only-side-effects.md">ADR-0056</see>
/// the coordinator no longer persists a synthesised dispatch response — the
/// platform stops inferring intent from terminal text. Every observable
/// effect a runtime has flows through platform tool calls; the messaging
/// tools persist their own <c>spring.messages</c> rows and emit their own
/// <see cref="ActivityEventType.MessageSent"/> activities. The coordinator's
/// role narrows to <em>"invoke dispatcher, observe outcome, emit lifecycle
/// activities, signal exit"</em>.
/// </para>
/// <para>
/// The coordinator is stateless with respect to any individual agent — it
/// operates entirely through the per-call delegates and the injected singleton
/// seams. This makes it safe to register as a singleton and share across all
/// <c>AgentActor</c> instances.
/// </para>
/// </remarks>
public class AgentDispatchCoordinator(
    IExecutionDispatcher executionDispatcher,
    ILogger<AgentDispatchCoordinator> logger) : IAgentDispatchCoordinator
{
    /// <summary>
    /// Reason string signalled through <c>onDispatchExit</c> on a clean
    /// runtime termination (exit code 0). Includes both the
    /// <see cref="ActivityEventType.RuntimeCompleted"/> and
    /// <see cref="ActivityEventType.RuntimeCompletedSilent"/> cases — both
    /// are clean exits from the actor's mailbox perspective.
    /// </summary>
    internal const string DispatchCompletedReason = "dispatch completed";

    /// <inheritdoc />
    public async Task RunDispatchAsync(
        string agentId,
        Message message,
        PromptAssemblyContext context,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        Func<string, Task> onDispatchExit,
        CancellationToken cancellationToken = default,
        IReadOnlyList<Message>? batch = null)
    {
        // #3056: deliver the whole pending set in one turn. message is the
        // representative (batch head) used for routing / correlation /
        // lifecycle activities; the full batch flows to the dispatcher so the
        // inbound envelope names every message. A null batch is a one-message
        // turn (the dispatcher falls back to [message]).
        var batchSize = batch?.Count ?? 1;
        try
        {
            // Phase: mailbox hands the message to the dispatcher (ADR-0056 §7).
            await emitActivity(
                BuildEvent(
                    agentId,
                    message.ThreadId,
                    ActivityEventType.MessageDispatchedToRuntime,
                    ActivitySeverity.Info,
                    batchSize > 1
                        ? $"Dispatched {batchSize} pending messages to runtime as one turn."
                        : "Message dispatched to runtime.",
                    details: JsonSerializer.SerializeToElement(new
                    {
                        agentId,
                        threadId = message.ThreadId,
                        messageId = message.Id,
                        batchSize,
                    })),
                CancellationToken.None);

            // Phase: runtime "started". The A2A dispatcher's launcher
            // surface does not expose a discrete container-start hook the
            // coordinator can subscribe to; the closest deterministic
            // moment is right before the DispatchAsync call, which is
            // when the dispatcher will (synchronously, from our vantage)
            // begin spinning up or selecting the runtime container.
            // Documented choice — promoting a real start hook is a future
            // refinement, not load-bearing for this PR.
            await emitActivity(
                BuildEvent(
                    agentId,
                    message.ThreadId,
                    ActivityEventType.RuntimeStarted,
                    ActivitySeverity.Info,
                    "Runtime started.",
                    details: JsonSerializer.SerializeToElement(new
                    {
                        agentId,
                        threadId = message.ThreadId,
                    })),
                CancellationToken.None);

            var outcome = await executionDispatcher.DispatchAsync(message, context, cancellationToken, batch);

            // Reasoning trace (when present, regardless of exit code) lands
            // BEFORE the terminal so consumers reading top-to-bottom see
            // reasoning first, terminal last.
            if (!string.IsNullOrEmpty(outcome.ReasoningTrace))
            {
                await emitActivity(
                    BuildEvent(
                        agentId,
                        message.ThreadId,
                        ActivityEventType.RuntimeReasoning,
                        ActivitySeverity.Info,
                        Summarise(outcome.ReasoningTrace!),
                        details: JsonSerializer.SerializeToElement(new
                        {
                            agentId,
                            threadId = message.ThreadId,
                            trace = outcome.ReasoningTrace,
                        })),
                    CancellationToken.None);
            }

            var toolCallCount = TryReadToolCallCount(outcome);

            if (outcome.ExitCode != 0)
            {
                logger.LogWarning(
                    "Dispatch for actor {ActorId} thread {ThreadId} exited with code {ExitCode}.",
                    agentId, message.ThreadId, outcome.ExitCode);

                await emitActivity(
                    BuildEvent(
                        agentId,
                        message.ThreadId,
                        ActivityEventType.RuntimeFailed,
                        ActivitySeverity.Error,
                        $"Runtime failed with exit code {outcome.ExitCode}.",
                        details: BuildTerminalDetails(agentId, message, outcome, toolCallCount)),
                    CancellationToken.None);

                // A connector-origin turn that failed is still a routing
                // outcome the activity stream must carry (#2560).
                await EmitConnectorRoutingDecisionAsync(
                    agentId, message, RoutingDisposition.Failed, emitActivity);

                await onDispatchExit($"dispatch exit code {outcome.ExitCode}");
                return;
            }

            // Clean exit. ADR-0056 §5: distinguish "the runtime did
            // something" from "the runtime did nothing".
            if (toolCallCount == 0)
            {
                logger.LogInformation(
                    "Dispatch for actor {ActorId} thread {ThreadId} exited cleanly with no tool calls (silent).",
                    agentId, message.ThreadId);

                await emitActivity(
                    BuildEvent(
                        agentId,
                        message.ThreadId,
                        ActivityEventType.RuntimeCompletedSilent,
                        ActivitySeverity.Warning,
                        "Runtime completed without invoking any tools.",
                        details: BuildTerminalDetails(agentId, message, outcome, toolCallCount)),
                    CancellationToken.None);
            }
            else
            {
                await emitActivity(
                    BuildEvent(
                        agentId,
                        message.ThreadId,
                        ActivityEventType.RuntimeCompleted,
                        ActivitySeverity.Info,
                        $"Runtime completed ({toolCallCount} tool call(s)).",
                        details: BuildTerminalDetails(agentId, message, outcome, toolCallCount)),
                    CancellationToken.None);
            }

            // Record the routing outcome of a connector-origin event (#2560).
            await EmitConnectorRoutingDecisionAsync(
                agentId, message, RoutingDisposition.Processed, emitActivity);

            // Per-thread dispatch is complete. The actor's mailbox drains
            // any messages appended during the dispatch (per-thread FIFO)
            // or marks the channel idle. Other threads on the same agent
            // are unaffected — concurrent threads run independently per
            // ADR-0030 §44.
            await onDispatchExit(DispatchCompletedReason);
        }
        catch (OperationCanceledException)
        {
            // A cancelled dispatch leaves the channel mid-drain. Signal
            // the exit so the mailbox doesn't sit Active-but-idle.
            logger.LogInformation(
                "Dispatch cancelled for actor {ActorId} thread {ThreadId}.",
                agentId, message.ThreadId);

            await onDispatchExit("dispatch cancelled");
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Dispatch failed for actor {ActorId} thread {ThreadId}.",
                agentId, message.ThreadId);

            await emitActivity(
                BuildEvent(
                    agentId,
                    message.ThreadId,
                    ActivityEventType.ErrorOccurred,
                    ActivitySeverity.Error,
                    $"Dispatch failed: {ex.Message}",
                    details: JsonSerializer.SerializeToElement(new
                    {
                        error = ex.Message,
                        agentId,
                        threadId = message.ThreadId,
                    })),
                CancellationToken.None);

            await onDispatchExit($"dispatch exception: {ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Reads the dispatcher-reported tool-call count from the outcome's
    /// diagnostics bag (<see cref="RuntimeOutcome.ToolCallCountKey"/>).
    /// Defaults to <c>0</c> when the dispatcher does not report one — the
    /// safer choice, because it surfaces the silent-completion case rather
    /// than hiding it behind a "couldn't tell" flag.
    /// </summary>
    private static int TryReadToolCallCount(RuntimeOutcome outcome)
    {
        if (outcome.Diagnostics is null)
        {
            return 0;
        }

        if (!outcome.Diagnostics.TryGetValue(RuntimeOutcome.ToolCallCountKey, out var raw)
            || raw is null)
        {
            return 0;
        }

        return raw switch
        {
            int i => i,
            long l => (int)l,
            JsonElement el when el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var n) => n,
            _ => int.TryParse(raw.ToString(), out var parsed) ? parsed : 0,
        };
    }

    private static JsonElement BuildTerminalDetails(
        string agentId,
        Message message,
        RuntimeOutcome outcome,
        int toolCallCount)
    {
        var doc = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["agentId"] = agentId,
            ["threadId"] = message.ThreadId,
            ["exitCode"] = outcome.ExitCode,
            ["durationMs"] = (long)outcome.Duration.TotalMilliseconds,
            ["toolCallCount"] = toolCallCount,
        };

        // Add a short reasoning-trace summary on the terminal so operators
        // see the first line of what the runtime produced without
        // expanding the separate RuntimeReasoning row.
        if (!string.IsNullOrEmpty(outcome.ReasoningTrace))
        {
            doc["reasoningTraceSummary"] = Summarise(outcome.ReasoningTrace!);
        }

        if (outcome.Diagnostics is not null)
        {
            foreach (var (key, value) in outcome.Diagnostics)
            {
                // Don't double-write the tool-call count or override the
                // canonical keys above.
                if (!doc.ContainsKey(key))
                {
                    doc[key] = value;
                }
            }
        }

        return JsonSerializer.SerializeToElement(doc);
    }

    /// <summary>
    /// Maximum length of the reasoning-trace summary surfaced on the
    /// <see cref="ActivityEventType.RuntimeReasoning"/> event's
    /// <see cref="ActivityEvent.Summary"/> and on the terminal event's
    /// <c>reasoningTraceSummary</c> detail. The full trace rides in the
    /// reasoning event's <c>trace</c> detail when capture-level allows.
    /// </summary>
    private const int ReasoningSummaryMaxLength = 240;

    private static string Summarise(string trace)
    {
        var trimmed = trace.Trim();
        if (trimmed.Length <= ReasoningSummaryMaxLength)
        {
            return trimmed;
        }

        return string.Concat(trimmed.AsSpan(0, ReasoningSummaryMaxLength - 1), "…");
    }

    /// <summary>
    /// The host-observable disposition of a connector-origin turn. The
    /// platform host records the routing <em>outcome</em> from its
    /// deterministic vantage point — it does not see the unit runtime's
    /// internal decision (delegate / no action), which is design work
    /// deferred to a follow-up (#2572).
    /// </summary>
    private enum RoutingDisposition
    {
        /// <summary>
        /// The unit's runtime processed the connector event and the turn
        /// reached its terminal — whether or not it dispatched a downstream
        /// agent. The "no agent dispatched" case lands here, which is the
        /// silent-stream gap #2560 closes.
        /// </summary>
        Processed,

        /// <summary>
        /// The unit's runtime container exited with a non-zero code while
        /// processing the connector event.
        /// </summary>
        Failed,
    }

    /// <summary>
    /// Emits a <see cref="ActivityEventType.DecisionMade"/> activity event
    /// recording the routing <em>outcome</em> of a connector-origin event
    /// (issue #2560). No-op for non-connector messages — agent-to-agent and
    /// human-originated turns are not connector routing decisions and would
    /// only add noise to the activity stream.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <see cref="ActivityEvent.CorrelationId"/> is the originating
    /// connector thread id, so this event, the unit's
    /// <see cref="ActivityEventType.MessageArrived"/>, any
    /// <see cref="ActivityEventType.DecisionMade"/> the runtime recorded via
    /// <c>sv.runtime.report_decision</c>, and the dispatched agent's
    /// own activity all share one correlation id — a single thread query
    /// reconstructs the full chain (#2560 acceptance criterion 3).
    /// </para>
    /// </remarks>
    private async Task EmitConnectorRoutingDecisionAsync(
        string agentId,
        Message message,
        RoutingDisposition disposition,
        Func<ActivityEvent, CancellationToken, Task> emitActivity)
    {
        if (!string.Equals(message.From.Scheme, Address.ConnectorScheme, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var reference = ConnectorEventReference.From(message);

        var decision = disposition == RoutingDisposition.Failed
            ? "processing_failed"
            : "event_processed";

        var summary = reference.EventType is { Length: > 0 } eventType
            ? $"Connector event '{eventType}' {DescribeDisposition(disposition)}."
            : $"Connector event {DescribeDisposition(disposition)}.";

        var details = JsonSerializer.SerializeToElement(new
        {
            decision,
            connectorEventType = reference.EventType,
            entityKind = reference.EntityKind,
            entityReference = reference.EntityReference,
            agentId,
            threadId = message.ThreadId,
            inboundMessageId = message.Id,
        });

        await emitActivity(
            BuildEvent(
                agentId,
                message.ThreadId,
                ActivityEventType.DecisionMade,
                disposition == RoutingDisposition.Failed
                    ? ActivitySeverity.Warning
                    : ActivitySeverity.Info,
                summary,
                details: details),
            CancellationToken.None);
    }

    private static string DescribeDisposition(RoutingDisposition disposition)
        => disposition == RoutingDisposition.Failed ? "processing failed" : "processed";

    private static ActivityEvent BuildEvent(
        string agentId,
        string? correlationId,
        ActivityEventType eventType,
        ActivitySeverity severity,
        string summary,
        JsonElement? details = null)
    {
        return new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            Address.For("agent", agentId),
            eventType,
            severity,
            summary,
            details,
            correlationId);
    }
}
