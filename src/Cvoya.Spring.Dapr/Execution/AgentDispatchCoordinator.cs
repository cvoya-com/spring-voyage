// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Routing;

using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of <see cref="IAgentDispatchCoordinator"/>.
/// Owns the execution-dispatch concern extracted from <c>AgentActor</c>:
/// invoking the <see cref="IExecutionDispatcher"/>, inspecting the response
/// for a non-zero container exit code, recording the response on its
/// originating thread, and signalling the per-thread dispatch exit so the
/// actor's mailbox can drain remaining queued messages on the same thread
/// or mark the channel idle.
/// </summary>
/// <remarks>
/// <para>
/// Domain messaging is one-way
/// (<see href="../../../docs/decisions/0048-event-vs-request-message-semantics.md">ADR-0048</see>):
/// the dispatch response is <b>recorded</b> on the originating thread via
/// <see cref="MessageRouter.PersistAsync"/> and is never routed back to
/// <see cref="Message.From"/>. A unit/agent that wants to respond acts
/// through its tools or sends a new one-way message.
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
    MessageRouter messageRouter,
    ILogger<AgentDispatchCoordinator> logger) : IAgentDispatchCoordinator
{
    /// <summary>
    /// Reason string signalled through <c>onDispatchExit</c> when the
    /// dispatcher returns a null response. Shared with
    /// <see cref="Actors.RuntimeInvocationPath"/> so the lean path can
    /// recognise this exit cause and surface a corresponding "no response"
    /// activity row. Promoted to an internal constant so the coordinator
    /// and consumers stay in lockstep — changing the literal in one place
    /// would silently disable the consumer's recognition logic (#2222).
    /// </summary>
    internal const string DispatchNoResponseReason = "dispatch returned no response";

    /// <inheritdoc />
    public async Task RunDispatchAsync(
        string agentId,
        Message message,
        PromptAssemblyContext context,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        Func<string, Task> onDispatchExit,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await executionDispatcher.DispatchAsync(message, context, cancellationToken);
            if (response is null)
            {
                logger.LogInformation(
                    "Dispatcher returned no response for thread {ThreadId}; nothing to record.",
                    message.ThreadId);

                // A connector-origin turn that produced no recordable
                // response is still a routing outcome (#2560): the unit
                // processed the connector event and the turn ended with
                // nothing to record. Emit the DecisionMade so the activity
                // stream is not silent on a connector event the unit
                // received. The neutral WorkflowStepCompleted "no response"
                // row is still emitted by the dispatch-exit handler below.
                await EmitConnectorRoutingDecisionAsync(
                    agentId, message, RoutingDisposition.Processed, emitActivity);

                // Even when the dispatcher returns nothing to record, the
                // dispatch is over from the actor's perspective. Signal the
                // per-thread exit so the mailbox can drain any messages
                // appended during the dispatch (per-thread FIFO) or mark
                // the channel idle.
                await onDispatchExit(DispatchNoResponseReason);
                return;
            }

            var dispatchExit = TryReadDispatchExit(response);
            if (dispatchExit is { ExitCode: not 0 } failure)
            {
                logger.LogWarning(
                    "Dispatch for actor {ActorId} thread {ThreadId} exited with code {ExitCode}: {StdErrFirstLine}",
                    agentId, message.ThreadId, failure.ExitCode, failure.StdErrFirstLine);

                var details = JsonSerializer.SerializeToElement(new
                {
                    exitCode = failure.ExitCode,
                    stderr = failure.StdErr,
                    agentId,
                    threadId = message.ThreadId,
                });

                await emitActivity(
                    BuildEvent(
                        agentId,
                        message.ThreadId,
                        ActivityEventType.ErrorOccurred,
                        ActivitySeverity.Error,
                        $"Container exit code {failure.ExitCode}: {failure.StdErrFirstLine}",
                        details: details),
                    CancellationToken.None);

                // Record the error response on the thread BEFORE signalling
                // the exit so it is ordered correctly in the thread timeline.
                // The error is also surfaced as the ErrorOccurred activity
                // above; recording the response keeps the agent's stderr/exit
                // payload on the durable thread record.
                await RecordResponseAsync(agentId, response, message.ThreadId, emitActivity, cancellationToken);

                // A connector-origin turn that failed is still a routing
                // outcome the activity stream must carry (#2560) — emit the
                // DecisionMade with the failed disposition so a thread query
                // reconstructs the chain even when the runtime crashed.
                await EmitConnectorRoutingDecisionAsync(
                    agentId, message, RoutingDisposition.Failed, emitActivity);

                await onDispatchExit($"dispatch exit code {failure.ExitCode}");
                return;
            }

            await RecordResponseAsync(agentId, response, message.ThreadId, emitActivity, cancellationToken);

            // Record the routing outcome of a connector-origin event (#2560).
            // The platform host cannot see what the unit's runtime decided
            // semantically (delegate / no action) without a runtime signal —
            // that distinction is design work deferred to #2572. What the host
            // CAN record deterministically, after the runtime invocation
            // returns, is that the connector event was processed and the turn
            // reached its terminal: a DecisionMade event so the activity
            // stream is no longer silent on the outcome — including the
            // "no agent dispatched" case. When the runtime recorded a routing
            // decision via sv.runtime.report_decision, that DecisionMade
            // (carrying the target) is also on the same thread, so a single
            // correlation query reconstructs the full chain.
            await EmitConnectorRoutingDecisionAsync(
                agentId, message, RoutingDisposition.Processed, emitActivity);

            // Per-thread dispatch is complete: the dispatcher returned a
            // response and we recorded it on the originating thread (domain
            // messaging is one-way — ADR-0048; the response is not routed
            // back to a recipient). The actor's mailbox drains any messages
            // appended during the dispatch (per-thread FIFO) or marks the
            // channel idle. Other threads on the same agent are unaffected —
            // concurrent threads run independently per ADR-0030 §44.
            await onDispatchExit("dispatch completed");
        }
        catch (OperationCanceledException)
        {
            // A cancelled dispatch leaves the channel mid-drain. Signal
            // the exit so the mailbox doesn't sit Active-but-idle: the
            // actor either drains the next queued message on the same
            // thread or marks the channel idle. Other threads are
            // unaffected (they have their own channels and dispatchers).
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
    /// Records the dispatch <paramref name="response"/> on its originating
    /// thread. Domain messaging is one-way (ADR-0048): the response is
    /// persisted to the thread timeline via
    /// <see cref="MessageRouter.PersistAsync"/> and is never routed back to a
    /// recipient. On success a neutral
    /// <see cref="ActivityEventType.WorkflowStepCompleted"/> activity is
    /// emitted so the dispatch terminal is visible in the activity feed; a
    /// persistence failure is surfaced as
    /// <see cref="ActivityEventType.ErrorOccurred"/> so the agent's output is
    /// not silently lost.
    /// </summary>
    private async Task RecordResponseAsync(
        string agentId,
        Message response,
        string? threadId,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        CancellationToken cancellationToken)
    {
        try
        {
            await messageRouter.PersistAsync(response, cancellationToken);

            // Neutral terminal activity: the dispatch produced a response and
            // it is recorded on the thread. Emitted with CancellationToken.None
            // so it lands even if the dispatch token is cancelled.
            await emitActivity(
                BuildEvent(
                    agentId,
                    threadId,
                    ActivityEventType.WorkflowStepCompleted,
                    ActivitySeverity.Info,
                    "Dispatch response recorded on thread.",
                    details: JsonSerializer.SerializeToElement(new
                    {
                        agentId,
                        threadId,
                        messageId = response.Id,
                    })),
                CancellationToken.None);
        }
        catch (Exception recordEx)
        {
            logger.LogWarning(recordEx,
                "Failed to record dispatcher response for thread {ThreadId}.",
                threadId);

            // A response that could not be persisted would otherwise be
            // invisible — surface it as an error activity so it is traceable
            // in the portal, not just buried in container logs.
            await emitActivity(
                BuildEvent(
                    agentId,
                    threadId,
                    ActivityEventType.ErrorOccurred,
                    ActivitySeverity.Error,
                    $"Failed to record dispatcher response: {recordEx.Message}",
                    details: JsonSerializer.SerializeToElement(new
                    {
                        error = recordEx.Message,
                        agentId,
                        threadId,
                    })),
                CancellationToken.None);
        }
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
    /// <see cref="ActivityEventType.MessageReceived"/>, any
    /// <see cref="ActivityEventType.DecisionMade"/> the runtime recorded via
    /// <c>sv.runtime.report_decision</c>, and the dispatched agent's
    /// own activity all share one correlation id — a single thread query
    /// reconstructs the full chain (#2560 acceptance criterion 3).
    /// </para>
    /// <para>
    /// <see cref="ActivityEvent.Details"/> carries the decision, the
    /// connector event type, and the external entity reference (e.g. a
    /// GitHub issue number) resolved from the connector payload via
    /// <see cref="ConnectorEventReference"/>. <c>dispatched_to</c> is
    /// intentionally absent on the host-side event: the host cannot see the
    /// runtime's delegation target — the runtime-callback
    /// <c>DecisionMade</c> carries it instead, correlated by the same
    /// thread id.
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

    private readonly record struct DispatchExit(int ExitCode, string? StdErr, string StdErrFirstLine);

    private static DispatchExit? TryReadDispatchExit(Message response)
    {
        try
        {
            if (response.Payload.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!response.Payload.TryGetProperty("ExitCode", out var exitProp) ||
                exitProp.ValueKind != JsonValueKind.Number ||
                !exitProp.TryGetInt32(out var exitCode))
            {
                return null;
            }

            string? stderr = null;
            if (response.Payload.TryGetProperty("Error", out var errProp) &&
                errProp.ValueKind == JsonValueKind.String)
            {
                stderr = errProp.GetString();
            }

            var firstLine = stderr is null
                ? string.Empty
                : stderr.Split('\n', 2)[0].TrimEnd('\r').Trim();

            return new DispatchExit(exitCode, stderr, firstLine);
        }
        catch
        {
            return null;
        }
    }

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
