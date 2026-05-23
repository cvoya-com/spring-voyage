// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

using Cvoya.Spring.Core.Lifecycle;

/// <summary>
/// Defines the types of activity events emitted by platform components.
/// </summary>
public enum ActivityEventType
{
    /// <summary>
    /// Emitted when a message lands in the recipient's mailbox (a routing
    /// event — the actor's <c>ReceiveAsync</c> path saw the envelope and
    /// queued it). Renamed from <c>MessageReceived</c> per
    /// <see href="../../../docs/decisions/0056-tool-only-side-effects.md">ADR-0056</see>
    /// §7 to tighten the semantic to "mailbox arrival" — the runtime has not
    /// necessarily been invoked yet; that is the separate
    /// <see cref="MessageDispatchedToRuntime"/> event. The enum ordinal is
    /// preserved (this is an in-place rename, not delete+append) per the
    /// actor-remoting wire-format ordinal-stability rule called out below.
    /// </summary>
    MessageArrived,
    MessageSent,
    ThreadStarted,
    DecisionMade,
    ErrorOccurred,
    StateChanged,
    InitiativeTriggered,
    ReflectionCompleted,
    WorkflowStepCompleted,
    CostIncurred,
    TokenDelta,

    /// <summary>
    /// Emitted when a Tier-2 reflection action is translated into a message
    /// and dispatched via <see cref="Messaging.IMessageRouter"/>. See #100.
    /// </summary>
    ReflectionActionDispatched,

    /// <summary>
    /// Emitted when a Tier-2 reflection action is rejected before dispatch —
    /// e.g. unknown action type, malformed payload, blocked by unit skill
    /// policy, or blocked by the agent's own <c>BlockedActions</c>. See #100.
    /// </summary>
    ReflectionActionSkipped,

    /// <summary>
    /// Emitted when a supervisor amendment is accepted (queued onto the
    /// agent's pending-amendments list). Paired with <see cref="DecisionMade"/>
    /// / <see cref="StateChanged"/> for StopAndWait amendments that also
    /// pause the active turn. See #142.
    /// </summary>
    AmendmentReceived,

    /// <summary>
    /// Emitted when a supervisor amendment is rejected — e.g. the sender is
    /// not a member unit, or the agent is disabled for that membership.
    /// See #142.
    /// </summary>
    AmendmentRejected,

    /// <summary>
    /// Emitted when the execution environment dispatches a tool / skill
    /// call. Details carry <c>toolName</c>, <c>callId</c>, and
    /// <c>arguments</c>. Paired with <see cref="ToolResult"/> via
    /// <c>callId</c>. See <see cref="Execution.StreamEvent.ToolCall"/>.
    /// </summary>
    ToolCall,

    /// <summary>
    /// Emitted when the execution environment receives a tool / skill
    /// result. Details carry <c>toolName</c>, <c>callId</c>, <c>isError</c>,
    /// and <c>result</c>. See <see cref="Execution.StreamEvent.ToolResult"/>.
    /// </summary>
    ToolResult,

    /// <summary>
    /// Emitted when a Tier-2 reflection action is surfaced as a proposal
    /// requiring human / unit confirmation rather than dispatched inline —
    /// the <see cref="Initiative.IAgentInitiativeEvaluator"/> returned
    /// <see cref="Initiative.InitiativeEvaluationDecision.ActWithConfirmation"/>.
    /// Details carry the translated target, the conversation id, the
    /// reason string, and whether the downgrade was fail-closed so
    /// operators can distinguish "operator asked for confirmation" from
    /// "a gate could not be evaluated." See #552.
    /// </summary>
    ReflectionActionProposed,

    /// <summary>
    /// Emitted by the <c>ArtefactValidationWorkflow</c> (T-04) as each probe step
    /// transitions through <c>Running</c> / <c>Succeeded</c> / <c>Failed</c>,
    /// with the unit address as <see cref="ActivityEvent.Source"/>. Payload
    /// carries at minimum <c>{step, status}</c> and, on failure,
    /// <c>{code}</c> from <see cref="Units.ArtefactValidationCodes"/>. The T-06
    /// web detail page and the T-07 validation panel subscribe to this type
    /// to render live progress without polling. APPENDED to the end of the
    /// enum per #956: the actor-remoting wire format serialises this enum
    /// by ordinal, so any mid-insert would silently renumber existing
    /// events — append is the safe operation.
    /// </summary>
    ValidationProgress,

    /// <summary>
    /// Emitted when an inbound connector webhook event is dropped by a
    /// per-binding filter before routing — e.g. the GitHub connector's
    /// label / author / path filters configured on
    /// <c>UnitGitHubConfig</c>. <see cref="ActivityEvent.Source"/> is the
    /// unit binding the filter is attached to. <see cref="ActivityEvent.Details"/>
    /// carries the connector slug, event type, the filter kind that
    /// matched (<c>exclude_label</c>, <c>include_label</c>,
    /// <c>include_author</c>, <c>include_path</c>), and the value(s)
    /// that drove the decision so operators can audit why a particular
    /// event was suppressed. Issue #2407.
    /// APPENDED to the end of the enum per #956 — the actor-remoting wire
    /// format serialises this enum by ordinal, so any mid-insert would
    /// silently renumber existing events; append is the safe operation.
    /// </summary>
    ConnectorEventFiltered,

    /// <summary>
    /// Emitted on every OTLP span the platform's
    /// <c>/otlp/v1/traces</c> ingest receives from a runtime container
    /// (issue #2492). <see cref="ActivityEvent.Source"/> is the subject
    /// the span is scoped to (the <c>sv.subject.uuid</c> resource
    /// attribute, scheme inferred from <c>sv.subject.kind</c>).
    /// <see cref="ActivityEvent.Details"/> carries the span name,
    /// kind, start/end timestamps, attributes, and any span events
    /// (subject to capture-level truncation applied server-side at
    /// ingest). APPENDED per #956.
    /// </summary>
    RuntimeSpan,

    /// <summary>
    /// Emitted on every OTLP log record the platform's
    /// <c>/otlp/v1/logs</c> ingest receives from a runtime container
    /// (issue #2492). Body text is captured at <c>full</c>, truncated
    /// to first/last N characters at <c>summary</c>, and dropped
    /// entirely at <c>off</c>. <see cref="ActivityEvent.Details"/>
    /// carries <c>severity_text</c>, <c>body</c>, <c>attributes</c>,
    /// plus the resource attributes the producer set. APPENDED per #956.
    /// </summary>
    RuntimeLog,

    /// <summary>
    /// Emitted when a runtime emits a free-text progress event via the
    /// <c>sv.progress</c> OTLP span event (issue #2492). The
    /// <see cref="ActivityEvent.Summary"/> carries the human-facing
    /// message; <see cref="ActivityEvent.Details"/> carries the source
    /// span id and any additional structured attributes. APPENDED per
    /// #956.
    /// </summary>
    RuntimeProgress,

    /// <summary>
    /// Emitted on every full LLM turn the runtime reports through OTLP
    /// (issue #2492). <see cref="ActivityEvent.Details"/> carries the
    /// model, prompt + completion text (subject to capture-level
    /// truncation), and token counts. Distinct from
    /// <see cref="CostIncurred"/>, which is the in-process cost
    /// accounting event emitted on the host side; <c>LlmTurn</c>
    /// captures what the runtime container's LLM call actually looked
    /// like. APPENDED per #956.
    /// </summary>
    LlmTurn,

    /// <summary>
    /// Emitted by the recipient when the mailbox hands an inbound
    /// <see cref="Messaging.Message"/> to the execution dispatcher
    /// (<see cref="Execution.IExecutionDispatcher.DispatchAsync"/>). Lands
    /// after <see cref="MessageArrived"/> (mailbox accept) and before the
    /// runtime-lifecycle events (<see cref="RuntimeStarted"/> etc.) so the
    /// activity stream reads "arrived → dispatched-to-runtime → started → …
    /// → terminal" in phase order. <see cref="ActivityEvent.Details"/>
    /// carries <c>agentId</c>, <c>threadId</c>, and the inbound message id.
    /// Added per
    /// <see href="../../../docs/decisions/0056-tool-only-side-effects.md">ADR-0056</see>
    /// §7. APPENDED per #956.
    /// </summary>
    MessageDispatchedToRuntime,

    /// <summary>
    /// Emitted by the recipient when the runtime container is observed to
    /// start (or, when the launcher exposes no start hook, alongside the
    /// dispatch call as the closest deterministic moment). Marks the
    /// transition from "mailbox handed off the message" to "the runtime is
    /// running". Paired with one of <see cref="RuntimeCompleted"/>,
    /// <see cref="RuntimeFailed"/>, or <see cref="RuntimeCompletedSilent"/>
    /// as the terminal. Added per
    /// <see href="../../../docs/decisions/0056-tool-only-side-effects.md">ADR-0056</see>
    /// §7. APPENDED per #956.
    /// </summary>
    RuntimeStarted,

    /// <summary>
    /// Emitted by the recipient when the runtime container exits with code
    /// 0 <em>and</em> made at least one platform tool call during the turn
    /// (i.e. it actually had an effect on the outside world). When the exit
    /// is clean but no tool calls were made, the platform emits
    /// <see cref="RuntimeCompletedSilent"/> instead so the stream is honest
    /// about the silent-completion gap. <see cref="ActivityEvent.Details"/>
    /// carries <c>exitCode</c>, <c>durationMs</c>, <c>toolCallCount</c>,
    /// and any dispatcher-specific diagnostics from
    /// <see cref="Execution.RuntimeOutcome.Diagnostics"/>. Added per
    /// <see href="../../../docs/decisions/0056-tool-only-side-effects.md">ADR-0056</see>
    /// §7. APPENDED per #956.
    /// </summary>
    RuntimeCompleted,

    /// <summary>
    /// Emitted by the recipient when the runtime container exits with a
    /// non-zero code. Replaces the legacy
    /// <see cref="ErrorOccurred"/> "Container exit code X" emission so the
    /// activity stream uses a typed event for runtime failure rather than a
    /// generic error row. <see cref="ActivityEvent.Details"/> carries
    /// <c>exitCode</c>, <c>durationMs</c>, <c>toolCallCount</c>, and any
    /// captured stderr / reasoning trace summary so operators can
    /// diagnose without leaving the activity feed. Added per
    /// <see href="../../../docs/decisions/0056-tool-only-side-effects.md">ADR-0056</see>
    /// §7. APPENDED per #956.
    /// </summary>
    RuntimeFailed,

    /// <summary>
    /// Emitted by the recipient when the runtime container exited
    /// successfully <em>without</em> invoking any platform tool calls — the
    /// runtime produced only terminal text and therefore had no observable
    /// effect on the outside world. Per
    /// <see href="../../../docs/decisions/0056-tool-only-side-effects.md">ADR-0056</see>
    /// §5 the platform <em>tolerates</em> this compliance gap rather than
    /// auto-wrapping the terminal text into a synthesised message: the
    /// activity makes the silence diagnosable (timestamp, duration, exit
    /// code, reasoning-trace summary) and the fix is a runtime/prompt
    /// improvement, not a host-side rescue. <see cref="ActivityEvent.Details"/>
    /// mirrors <see cref="RuntimeCompleted"/> with
    /// <c>toolCallCount = 0</c>. APPENDED per #956.
    /// </summary>
    RuntimeCompletedSilent,

    /// <summary>
    /// Emitted by the recipient carrying the runtime's captured terminal
    /// text (stdout, A2A task reply, or file-capture buffer — whichever
    /// <see cref="Execution.AgentResponseCapture"/> mode the launcher
    /// selected). Diagnostic only — the reasoning trace is never routed as
    /// a message; it lets humans debugging a turn see what the runtime
    /// concluded before calling its tools. Volume is controlled by the
    /// OTLP capture-level knob from
    /// <see href="../../../docs/decisions/0054-one-mcp-server-one-execution-host.md">ADR-0054</see>
    /// (default <c>summary</c>: trace truncated; <c>full</c>: trace
    /// verbatim; <c>off</c>: event suppressed). Added per
    /// <see href="../../../docs/decisions/0056-tool-only-side-effects.md">ADR-0056</see>
    /// §7. APPENDED per #956.
    /// </summary>
    RuntimeReasoning,
}
