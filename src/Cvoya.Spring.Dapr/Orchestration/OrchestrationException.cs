// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Orchestration;

public sealed class OrchestrationException : Exception
{
    public OrchestrationException(string rejectCode, string message)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(rejectCode);

        RejectCode = rejectCode;
    }

    public OrchestrationException(string rejectCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(rejectCode);

        RejectCode = rejectCode;
    }

    public string RejectCode { get; }

    public static class RejectCodes
    {
        public const string OrchestrationSelfDelegation = nameof(OrchestrationSelfDelegation);

        // A message-delivery tool call was malformed — e.g. sv.messaging.broadcast
        // given neither or both of 'addresses' / 'scope', or a 'scope' that does
        // not apply to the caller. Caught by synchronous validation before any
        // delivery attempt; surfaced as a validation-class tool error (HTTP 400).
        public const string OrchestrationInvalidRequest = nameof(OrchestrationInvalidRequest);

        // ADR-0049 §6 — terminal delivery failure. The dispatcher's
        // synchronous bounded-retry delivery loop exhausted its R/T budget
        // against transient infrastructure. Surfaced as a tool error telling
        // the calling model the platform is degraded and to retry.
        public const string OrchestrationDeliveryFailed = nameof(OrchestrationDeliveryFailed);

        // ADR-0049 / #2576 — raised by MessageDeliveryService when a thread's
        // per-thread hop counter (the ThreadHopActor) exceeds
        // OrchestrationDeliveryOptions.MaxHopCount. This replaces the
        // call-stack depth guard removed under ADR-0049: one-way delivery has
        // no call stack, so the guard is carried on the message thread instead.
        public const string OrchestrationDepthExceeded = nameof(OrchestrationDepthExceeded);

        // ADR-0039 §3 gate 6 — cross-tenant containment. The token's
        // tenantId claim must match the resolved tenant of the caller (and,
        // when applicable, the target). Any mismatch surfaces with this
        // reject code and an HTTP 403 from the dispatcher endpoint.
        public const string OrchestrationCrossTenant = nameof(OrchestrationCrossTenant);
    }
}
