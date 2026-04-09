/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Core.Capabilities;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Represents an observable activity event emitted by a component.
/// </summary>
/// <param name="Id">The unique identifier of the event.</param>
/// <param name="Timestamp">The timestamp when the event occurred.</param>
/// <param name="Source">The address of the component that emitted the event.</param>
/// <param name="EventType">The type of activity event.</param>
/// <param name="Description">A human-readable description of the event.</param>
public record ActivityEvent(
    Guid Id,
    DateTimeOffset Timestamp,
    Address Source,
    string EventType,
    string Description);
