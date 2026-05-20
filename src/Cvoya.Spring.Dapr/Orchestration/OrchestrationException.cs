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

        // ADR-0049 §6 — terminal delivery failure. The dispatcher's
        // synchronous bounded-retry delivery loop exhausted its R/T budget
        // against transient infrastructure. Surfaced as a tool error telling
        // the calling model the platform is degraded and to retry.
        public const string OrchestrationDeliveryFailed = nameof(OrchestrationDeliveryFailed);

        // ADR-0049 — retained for the message-carried hop counter follow-up
        // (#2576). OrchestrationDepthCounter, the call-stack-scoped guard
        // that previously raised this code, was deleted because it is
        // ineffective under one-way delivery; the code itself is reserved
        // for its replacement and is intentionally unraised in the interim.
        public const string OrchestrationDepthExceeded = nameof(OrchestrationDepthExceeded);

        // ADR-0039 §3 gate 6 — cross-tenant containment. The token's
        // tenantId claim must match the resolved tenant of the caller (and,
        // when applicable, the target). Any mismatch surfaces with this
        // reject code and an HTTP 403 from the dispatcher endpoint.
        public const string OrchestrationCrossTenant = nameof(OrchestrationCrossTenant);
    }
}
