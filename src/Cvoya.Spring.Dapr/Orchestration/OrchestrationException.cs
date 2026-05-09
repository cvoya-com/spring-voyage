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
        public const string OrchestrationCallerIsNotUnit = nameof(OrchestrationCallerIsNotUnit);
        public const string OrchestrationTargetNotChild = nameof(OrchestrationTargetNotChild);
        public const string OrchestrationSelfDelegation = nameof(OrchestrationSelfDelegation);
        public const string OrchestrationDepthExceeded = nameof(OrchestrationDepthExceeded);

        // ADR-0039 §3 gate 6 — cross-tenant containment. The token's
        // tenantId claim must match the resolved tenant of the caller (and,
        // when applicable, the target). Any mismatch surfaces with this
        // reject code and an HTTP 403 from the dispatcher endpoint.
        public const string OrchestrationCrossTenant = nameof(OrchestrationCrossTenant);
    }
}
