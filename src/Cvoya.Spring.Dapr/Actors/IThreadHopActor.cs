// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;

/// <summary>
/// Dapr actor interface for the per-thread message-delivery hop counter
/// (#2576). One actor instance exists per message thread; its actor id is
/// the thread id. The actor holds a single <see cref="int"/> of state — the
/// number of message-delivery hops taken on the thread so far.
/// </summary>
/// <remarks>
/// <para>
/// ADR-0049 removed the call-stack depth guard: under one-way message
/// delivery there is no synchronous call stack to count, so a fan-out cycle
/// (A delivers to B, B delivers to A, …) cannot be bounded by stack depth.
/// The hop counter replaces it — it is carried on the message thread, so
/// every delivery on a thread, no matter how many actors participate,
/// increments the same counter. <see cref="Cvoya.Spring.Dapr.Orchestration.MessageDeliveryService"/>
/// increments the counter once per <c>sv.messaging.send</c> /
/// <c>sv.messaging.broadcast</c> call and rejects the delivery once the
/// count exceeds <see cref="Cvoya.Spring.Dapr.Orchestration.OrchestrationDeliveryOptions.MaxHopCount"/>.
/// </para>
/// <para>
/// Storage: the count lives in the actor's own Dapr state store (the same
/// state-store component the agent / unit actors use). A single virtual
/// actor keyed by thread id serialises all increments for that thread, so
/// concurrent deliveries on one thread cannot race the counter — Dapr's
/// per-actor turn-based concurrency is the synchronisation primitive. The
/// state row is durable for the life of the thread; it is not actively
/// reclaimed (a thread is a bounded conversation and the rows are tiny).
/// </para>
/// </remarks>
public interface IThreadHopActor : IActor
{
    /// <summary>
    /// Atomically increments this thread's message-delivery hop counter and
    /// returns the new value. The first call on a fresh thread returns
    /// <c>1</c>.
    /// </summary>
    Task<int> IncrementAsync();
}
