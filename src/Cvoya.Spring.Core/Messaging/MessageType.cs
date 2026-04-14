// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

/// <summary>
/// Defines the types of messages exchanged between addressable components.
/// </summary>
public enum MessageType
{
    /// <summary>
    /// A domain-specific message carrying business logic payload.
    /// </summary>
    Domain,

    /// <summary>
    /// A cancellation request for an in-progress operation.
    /// </summary>
    Cancel,

    /// <summary>
    /// A query requesting the current status of the receiver.
    /// </summary>
    StatusQuery,

    /// <summary>
    /// A health check probe.
    /// </summary>
    HealthCheck,

    /// <summary>
    /// A policy update notification.
    /// </summary>
    PolicyUpdate,

    /// <summary>
    /// A mid-flight amendment from a supervisor (parent unit or the agent
    /// itself) pushing an instruction into a live agent turn without
    /// resetting its context. See #142. The payload is an
    /// <see cref="AmendmentPayload"/>; the recipient queues low-priority
    /// amendments to be picked up between tool calls and breaks out of the
    /// current turn for <c>StopAndWait</c> priority.
    /// </summary>
    Amendment,
}