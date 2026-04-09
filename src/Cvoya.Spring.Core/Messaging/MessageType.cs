/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

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
