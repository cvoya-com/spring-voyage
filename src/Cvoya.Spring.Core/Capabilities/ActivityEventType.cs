// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Defines the types of activity events emitted by platform components.
/// </summary>
public enum ActivityEventType
{
    MessageReceived,
    MessageSent,
    ConversationStarted,
    ConversationCompleted,
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
    /// Emitted by the <c>UnitValidationWorkflow</c> (T-04) as each probe step
    /// transitions through <c>Running</c> / <c>Succeeded</c> / <c>Failed</c>,
    /// with the unit address as <see cref="ActivityEvent.Source"/>. Payload
    /// carries at minimum <c>{step, status}</c> and, on failure,
    /// <c>{code}</c> from <see cref="Units.UnitValidationCodes"/>. The T-06
    /// web detail page and the T-07 validation panel subscribe to this type
    /// to render live progress without polling. APPENDED to the end of the
    /// enum per #956: the actor-remoting wire format serialises this enum
    /// by ordinal, so any mid-insert would silently renumber existing
    /// events — append is the safe operation.
    /// </summary>
    ValidationProgress,
}