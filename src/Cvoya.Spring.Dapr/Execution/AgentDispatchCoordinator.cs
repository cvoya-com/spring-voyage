// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
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
        IReadOnlyList<Message>? batch = null,
        string? costAttributionAgentId = null)
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

            // #3124: ingest the sidecar's parsed stream-json events as activity
            // events BEFORE the terminal so a CLI-runtime turn (claude-code /
            // gemini / codex) is observable in the portal Activity feed and via
            // `spring tail`: one ToolCall per tool the parser saw, and a
            // RuntimeLog for any captured stderr (so non-structured runtime
            // output is never silently dropped). Lands after RuntimeReasoning,
            // before cost / terminal.
            await EmitStreamRuntimeEventsAsync(agentId, message, outcome, emitActivity);

            // #3073: emit the turn's cost (when the runtime reported one) BEFORE
            // the terminal, regardless of exit code — a failed turn still bills.
            // The CostIncurred activity is what the cost ledger (CostRecord) and
            // the BudgetEnforcer consume; without it both sit idle and spend
            // always reads $0. #3075 threads the cost-attribution agent (a
            // clone's parent) and the initiative classification.
            await EmitCostIncurredAsync(agentId, message, outcome, emitActivity, costAttributionAgentId);

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

    /// <summary>
    /// Ingests the sidecar's parsed stream-json events (#3124) as activity
    /// events so a CLI-runtime turn is observable in the portal Activity feed
    /// and via <c>spring tail</c>. Emits one
    /// <see cref="ActivityEventType.ToolCall"/> per tool name the parser
    /// observed (<see cref="RuntimeOutcome.StreamToolCallsKey"/>), and a single
    /// <see cref="ActivityEventType.RuntimeLog"/> at
    /// <see cref="ActivitySeverity.Warning"/> for any captured stderr
    /// (<see cref="RuntimeOutcome.RuntimeStderrKey"/>) so non-structured runtime
    /// output is never silently dropped. All events share the inbound message's
    /// thread id as their correlation id, so they filter alongside the turn's
    /// other activities. No-op when the diagnostics carry neither key (a
    /// text-mode runtime, or a turn with no tool calls and no stderr).
    /// </summary>
    /// <remarks>
    /// This is the general Claude/Gemini/Codex mapping. The sidecar's
    /// stream-json parser surfaces tool-call <em>names</em> only — not call
    /// ids, arguments, or results — so this emits <c>ToolCall</c> without a
    /// paired <c>ToolResult</c>, and does not synthesise a separate
    /// <c>LlmTurn</c> (the assistant text already rides the
    /// <see cref="ActivityEventType.RuntimeReasoning"/> event). Enriching the
    /// parser to carry call ids / arguments / results — and the matching
    /// <c>ToolResult</c> / <c>LlmTurn</c> events — is the larger event-taxonomy
    /// work tracked as a follow-up (#3138).
    /// </remarks>
    private static async Task EmitStreamRuntimeEventsAsync(
        string agentId,
        Message message,
        RuntimeOutcome outcome,
        Func<ActivityEvent, CancellationToken, Task> emitActivity)
    {
        if (outcome.Diagnostics is null)
        {
            return;
        }

        var toolNames = ReadDiagnosticStringList(outcome.Diagnostics, RuntimeOutcome.StreamToolCallsKey);
        for (var i = 0; i < toolNames.Count; i++)
        {
            var toolName = toolNames[i];
            await emitActivity(
                BuildEvent(
                    agentId,
                    message.ThreadId,
                    ActivityEventType.ToolCall,
                    ActivitySeverity.Info,
                    $"Tool call: {toolName}",
                    details: JsonSerializer.SerializeToElement(new
                    {
                        toolName,
                        // The sidecar reports names only — no call id / arguments
                        // (see the method remarks / #3138). callIndex preserves
                        // the observed order so consumers can sequence the calls.
                        callIndex = i,
                        agentId,
                        threadId = message.ThreadId,
                    })),
                CancellationToken.None);
        }

        var stderr = ReadDiagnosticString(outcome.Diagnostics, RuntimeOutcome.RuntimeStderrKey);
        if (!string.IsNullOrEmpty(stderr))
        {
            await emitActivity(
                BuildEvent(
                    agentId,
                    message.ThreadId,
                    ActivityEventType.RuntimeLog,
                    ActivitySeverity.Warning,
                    Summarise(stderr),
                    details: JsonSerializer.SerializeToElement(new
                    {
                        body = stderr,
                        severity_text = "WARNING",
                        agentId,
                        threadId = message.ThreadId,
                    })),
                CancellationToken.None);
        }
    }

    /// <summary>
    /// Reads a string list off the diagnostics bag (e.g.
    /// <see cref="RuntimeOutcome.StreamToolCallsKey"/>). Tolerates the value
    /// arriving as a <see cref="IReadOnlyList{T}"/> / <see cref="IEnumerable{T}"/>
    /// of strings (the in-process path) or a JSON array (a serialised path).
    /// Returns an empty list when the key is absent or unreadable.
    /// </summary>
    private static IReadOnlyList<string> ReadDiagnosticStringList(
        IReadOnlyDictionary<string, object?> diagnostics, string key)
    {
        if (!diagnostics.TryGetValue(key, out var raw) || raw is null)
        {
            return Array.Empty<string>();
        }

        switch (raw)
        {
            case IReadOnlyList<string> list:
                return list;
            case IEnumerable<string> seq:
                return seq.ToArray();
            case JsonElement el when el.ValueKind == JsonValueKind.Array:
                var fromJson = new List<string>(el.GetArrayLength());
                foreach (var item in el.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var s = item.GetString();
                        if (!string.IsNullOrEmpty(s))
                        {
                            fromJson.Add(s);
                        }
                    }
                }
                return fromJson;
            default:
                return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Emits a <see cref="ActivityEventType.CostIncurred"/> activity for the
    /// turn when the dispatcher reported a cost on the outcome's diagnostics
    /// (<see cref="RuntimeOutcome.CostUsdKey"/>, populated from the Claude Code
    /// CLI's <c>total_cost_usd</c> via the sidecar — #3073). This is the live
    /// producer the cost ledger (<c>CostTracker</c> → <c>CostRecord</c>) and the
    /// <c>BudgetEnforcer</c> consume; the event's <see cref="ActivityEvent.Cost"/>
    /// and <c>details</c> (model / tokens / tenant / unit / costSource) match the
    /// shape <c>CostTracker.MapToRecord</c> reads. No-op when no cost was
    /// reported (a text-mode runtime or a zero-cost turn).
    /// </summary>
    /// <remarks>
    /// #3075: two attribution refinements over the #3073 baseline.
    /// <list type="bullet">
    /// <item>
    /// <description>
    /// <b>Clone roll-up.</b> When <paramref name="costAttributionAgentId"/> is
    /// set (the actor passes the parent agent id for an ephemeral clone), the
    /// cost event's <see cref="ActivityEvent.Source"/> is the parent — so
    /// <c>CostRecord.AgentId</c> is the parent and the clone's spend rolls up
    /// to the parent's cost rollup. The clone's own id is preserved in
    /// <c>details.cloneAgentId</c> for traceability.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// <b>Initiative classification.</b> A turn triggered by a self-addressed
    /// initiative (Tier-2 reflection) message
    /// (<see cref="MessageProvenance.Initiative"/> and <c>From</c> is the
    /// dispatching agent) bills as <see cref="CostSource.Initiative"/>; every
    /// other turn stays <see cref="CostSource.Work"/>. A reflection message
    /// addressed to a different agent triggers normal work on the recipient
    /// (it is responding), so it is not reclassified.
    /// </description>
    /// </item>
    /// </list>
    /// </remarks>
    private static async Task EmitCostIncurredAsync(
        string agentId,
        Message message,
        RuntimeOutcome outcome,
        Func<ActivityEvent, CancellationToken, Task> emitActivity,
        string? costAttributionAgentId)
    {
        if (outcome.Diagnostics is null)
        {
            return;
        }

        var cost = ReadDiagnosticDecimal(outcome.Diagnostics, RuntimeOutcome.CostUsdKey);
        if (cost is not > 0m)
        {
            return;
        }

        var model = ReadDiagnosticString(outcome.Diagnostics, RuntimeOutcome.ModelKey) ?? "unknown";
        var inputTokens = ReadDiagnosticInt(outcome.Diagnostics, RuntimeOutcome.InputTokensKey);
        var outputTokens = ReadDiagnosticInt(outcome.Diagnostics, RuntimeOutcome.OutputTokensKey);
        var tenantId = ReadDiagnosticString(outcome.Diagnostics, "tenantId");
        var unitId = ReadDiagnosticString(outcome.Diagnostics, "unitId");

        // Clone roll-up: bill the parent when an attribution target is given,
        // otherwise the dispatching agent itself.
        var billedAgentId = string.IsNullOrEmpty(costAttributionAgentId)
            ? agentId
            : costAttributionAgentId!;
        var cloneAgentId = string.IsNullOrEmpty(costAttributionAgentId) ? null : agentId;

        var costSource = ClassifyCostSource(agentId, message);

        var details = JsonSerializer.SerializeToElement(new
        {
            model,
            inputTokens,
            outputTokens,
            costSource = costSource.ToString(),
            tenantId,
            unitId,
            // Present only for a clone turn rolled up to its parent (#3075).
            cloneAgentId,
            durationMs = (long)outcome.Duration.TotalMilliseconds,
        });

        var summary =
            $"Cost incurred: {cost.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)} USD "
            + $"({model}, {inputTokens} in / {outputTokens} out)";

        var costEvent = new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            Address.For("agent", billedAgentId),
            ActivityEventType.CostIncurred,
            ActivitySeverity.Info,
            summary,
            details,
            message.ThreadId,
            cost.Value);

        await emitActivity(costEvent, CancellationToken.None);
    }

    /// <summary>
    /// Classifies a turn's cost source from the inbound message's provenance
    /// (#3075). A self-addressed initiative message — one the agent's own
    /// Tier-2 reflection loop produced and routed back to itself — bills as
    /// <see cref="CostSource.Initiative"/>; everything else is
    /// <see cref="CostSource.Work"/>. The <c>From</c>-is-self check keeps a
    /// reflection message addressed to a <em>different</em> agent classified as
    /// the recipient's Work (the recipient is responding, not self-initiating).
    /// </summary>
    private static CostSource ClassifyCostSource(string agentId, Message message)
    {
        if (message.Provenance != MessageProvenance.Initiative)
        {
            return CostSource.Work;
        }

        // Only the originating agent's own initiative turn is Initiative cost.
        return GuidFormatter.TryParse(agentId, out var agentGuid) && message.From.Id == agentGuid
            ? CostSource.Initiative
            : CostSource.Work;
    }

    private static decimal? ReadDiagnosticDecimal(IReadOnlyDictionary<string, object?> diagnostics, string key)
    {
        if (!diagnostics.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            decimal d => d,
            double db => (decimal)db,
            float f => (decimal)f,
            long l => l,
            int i => i,
            JsonElement el when el.ValueKind == JsonValueKind.Number && el.TryGetDecimal(out var n) => n,
            _ => decimal.TryParse(
                raw.ToString(),
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed)
                    ? parsed
                    : null,
        };
    }

    private static int ReadDiagnosticInt(IReadOnlyDictionary<string, object?> diagnostics, string key)
    {
        if (!diagnostics.TryGetValue(key, out var raw) || raw is null)
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

    private static string? ReadDiagnosticString(IReadOnlyDictionary<string, object?> diagnostics, string key)
    {
        if (!diagnostics.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        return raw switch
        {
            string s => s,
            JsonElement el when el.ValueKind == JsonValueKind.String => el.GetString(),
            _ => raw.ToString(),
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
