// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

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