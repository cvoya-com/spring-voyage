// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Messaging;

/// <summary>
/// Platform defaults for the synchronous bounded-retry delivery loop in
/// <see cref="MessageDeliveryService"/> (ADR-0049 §4). A message-delivery
/// tool delivers inline; a transient infrastructure failure of the fast
/// mailbox enqueue is retried up to <see cref="MaxAttempts"/> times within
/// the <see cref="Budget"/> window with backoff. The defaults are deliberately
/// small — the only thing that can fail a mailbox enqueue is a transient Dapr
/// hiccup, and a delivery is one fast actor hop (ADR-0049 §5).
/// </summary>
public sealed class MessageDeliveryOptions
{
    /// <summary>Maximum number of delivery attempts (the initial try plus retries).</summary>
    public int MaxAttempts { get; set; } = DefaultMaxAttempts;

    /// <summary>Total wall-clock budget across all attempts and backoff waits.</summary>
    public TimeSpan Budget { get; set; } = DefaultBudget;

    /// <summary>Initial backoff delay; doubled after each failed attempt.</summary>
    public TimeSpan InitialBackoff { get; set; } = DefaultInitialBackoff;

    /// <summary>Default number of delivery attempts — three.</summary>
    public const int DefaultMaxAttempts = 3;

    /// <summary>Default total delivery budget — roughly five seconds.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);

    /// <summary>Default initial backoff between delivery attempts.</summary>
    public static readonly TimeSpan DefaultInitialBackoff = TimeSpan.FromMilliseconds(250);
}
