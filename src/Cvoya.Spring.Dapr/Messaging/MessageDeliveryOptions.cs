// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Messaging;

/// <summary>
/// Platform defaults for the synchronous bounded-retry delivery loop in
/// <see cref="MessageDeliveryService"/> (ADR-0053 §4). A message-delivery
/// tool delivers inline; a transient infrastructure failure of the fast
/// mailbox enqueue is retried up to <see cref="MaxAttempts"/> times within
/// the <see cref="Budget"/> window with backoff. The defaults are deliberately
/// small — the only thing that can fail a mailbox enqueue is a transient Dapr
/// hiccup, and a delivery is one fast actor hop (ADR-0053 §4).
/// <para>
/// <see cref="PerAttemptTimeout"/> bounds each individual
/// <c>proxy.ReceiveAsync</c> attempt (#3004). Without it a single attempt can
/// hang until the Dapr actor proxy's default <c>HttpClient.Timeout</c> (~100s)
/// when the target actor is busy under load — the fast-enqueue invariant
/// (ADR-0053 §4) does not hold in practice while the ~88s A2A hang (#3002) is
/// open. A hit timeout is treated as a transient failure and retried within
/// <see cref="Budget"/>, so a brief hiccup still delivers while a sustained
/// hang surfaces a per-recipient <c>delivered:false</c> in a few seconds
/// rather than hanging the agent's turn for ~100s.
/// </para>
/// </summary>
public sealed class MessageDeliveryOptions
{
    /// <summary>Maximum number of delivery attempts (the initial try plus retries).</summary>
    public int MaxAttempts { get; set; } = DefaultMaxAttempts;

    /// <summary>Total wall-clock budget across all attempts and backoff waits.</summary>
    public TimeSpan Budget { get; set; } = DefaultBudget;

    /// <summary>Initial backoff delay; doubled after each failed attempt.</summary>
    public TimeSpan InitialBackoff { get; set; } = DefaultInitialBackoff;

    /// <summary>
    /// Maximum duration of a single <c>proxy.ReceiveAsync</c> attempt before it
    /// is cancelled and retried as a transient failure (#3004). Each attempt is
    /// effectively bounded by <c>min(PerAttemptTimeout, remaining Budget)</c>.
    /// </summary>
    public TimeSpan PerAttemptTimeout { get; set; } = DefaultPerAttemptTimeout;

    /// <summary>Default number of delivery attempts — three.</summary>
    public const int DefaultMaxAttempts = 3;

    /// <summary>Default total delivery budget — roughly five seconds.</summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(5);

    /// <summary>Default initial backoff between delivery attempts.</summary>
    public static readonly TimeSpan DefaultInitialBackoff = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Default per-attempt timeout — two seconds. Generous against the
    /// millisecond-scale fast enqueue, but short enough that ~two attempts fit
    /// inside the five-second <see cref="DefaultBudget"/> and the agent never
    /// blocks near the ~100s Dapr <c>HttpClient.Timeout</c>.
    /// </summary>
    public static readonly TimeSpan DefaultPerAttemptTimeout = TimeSpan.FromSeconds(2);
}
