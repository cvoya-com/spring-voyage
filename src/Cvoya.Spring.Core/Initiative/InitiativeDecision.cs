// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

/// <summary>
/// The outcome of a Tier 1 screening evaluation for an incoming event.
/// </summary>
public enum InitiativeDecision
{
    /// <summary>
    /// The event is not relevant — no further processing needed.
    /// </summary>
    Ignore,

    /// <summary>
    /// The event is potentially relevant — queue it for later Tier 2 reflection.
    /// </summary>
    QueueForReflection,

    /// <summary>
    /// The event requires immediate attention — invoke Tier 2 cognition now.
    /// </summary>
    ActImmediately,
}