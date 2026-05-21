// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors.Runtime;

/// <summary>
/// Dapr virtual actor for the per-thread message-delivery hop counter
/// (#2576). The actor id is the message thread id; the actor holds a single
/// <see cref="int"/> of durable state — the number of message-delivery hops
/// taken on the thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>Storage choice.</b> The count is persisted in the actor's own Dapr
/// state store under <see cref="HopCountStateKey"/>. A virtual actor keyed
/// by thread id is the natural home for a per-thread integer: Dapr's
/// turn-based per-actor concurrency serialises every <see cref="IncrementAsync"/>
/// call for a given thread, so concurrent <c>sv.messaging.send</c> /
/// <c>sv.messaging.broadcast</c> deliveries on one thread cannot race the
/// counter without any explicit lock. The row is durable across actor
/// deactivation, so the bound holds even when the thread spans process
/// restarts.
/// </para>
/// </remarks>
public class ThreadHopActor(ActorHost host) : Actor(host), IThreadHopActor
{
    /// <summary>Actor-state key holding this thread's hop count.</summary>
    public const string HopCountStateKey = "ThreadHop:Count";

    /// <inheritdoc />
    public async Task<int> IncrementAsync()
    {
        var current = await StateManager.TryGetStateAsync<int>(HopCountStateKey);
        var next = (current.HasValue ? current.Value : 0) + 1;
        await StateManager.SetStateAsync(HopCountStateKey, next);
        return next;
    }
}
