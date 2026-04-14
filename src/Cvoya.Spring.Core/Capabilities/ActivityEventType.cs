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
}