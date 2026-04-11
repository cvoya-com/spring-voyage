// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Initiative;

/// <summary>
/// Defines the levels of autonomous initiative an agent can exercise.
/// Higher levels grant broader control scope and require more permissions.
/// </summary>
public enum InitiativeLevel
{
    /// <summary>
    /// No initiative. The agent only acts when explicitly activated by external triggers.
    /// </summary>
    Passive,

    /// <summary>
    /// Monitors events via fixed triggers. Decides whether to act on each event.
    /// </summary>
    Attentive,

    /// <summary>
    /// Adjusts its own trigger frequency. Chooses actions from an allowed set.
    /// May modify its own reminder schedule. Requires <c>reminder.modify</c> permission.
    /// </summary>
    Proactive,

    /// <summary>
    /// Creates its own triggers, manages subscriptions and activation configuration.
    /// Full self-direction. Requires <c>topic.subscribe</c> and <c>activation.modify</c> permissions.
    /// </summary>
    Autonomous,
}