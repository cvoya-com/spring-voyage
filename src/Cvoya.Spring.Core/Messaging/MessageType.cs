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
    PolicyUpdate
}