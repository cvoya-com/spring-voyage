// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Messaging;

public sealed class MessageDeliveryException : Exception
{
    public MessageDeliveryException(string rejectCode, string message)
        : base(message)
    {
        ArgumentNullException.ThrowIfNull(rejectCode);

        RejectCode = rejectCode;
    }

    public MessageDeliveryException(string rejectCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(rejectCode);

        RejectCode = rejectCode;
    }

    public string RejectCode { get; }

    public static class RejectCodes
    {
        // A caller addressed itself — a message-delivery tool may not deliver
        // to its own mailbox. Caught by synchronous validation before any
        // delivery attempt.
        public const string SelfDelivery = nameof(SelfDelivery);

        // A message-delivery tool call was malformed — e.g. sv.messaging.multicast
        // given neither or both of 'addresses' / 'scope', or a 'scope' that does
        // not apply to the caller. Caught by synchronous validation before any
        // delivery attempt; surfaced as a validation-class tool error (HTTP 400).
        public const string InvalidRequest = nameof(InvalidRequest);

        // ADR-0049 §6 — terminal delivery failure. The dispatcher's
        // synchronous bounded-retry delivery loop exhausted its R/T budget
        // against transient infrastructure. Surfaced as a tool error telling
        // the calling model the platform is degraded and to retry.
        public const string DeliveryFailed = nameof(DeliveryFailed);

        // ADR-0039 §3 gate 6 — cross-tenant containment. The token's
        // tenantId claim must match the resolved tenant of the caller (and,
        // when applicable, the target). Any mismatch surfaces with this
        // reject code and an HTTP 403 from the dispatcher endpoint.
        public const string CrossTenant = nameof(CrossTenant);

        // A messaging tool addressed a target whose scheme is non-routable.
        // The connector:// scheme is the sole v0.1 case: connectors stamp
        // message provenance on inbound webhook events but cannot receive
        // messages. Surfaced before any delivery attempt so the calling
        // model can pick a routable participant (agent / unit / human).
        public const string UnroutableTarget = nameof(UnroutableTarget);
    }
}
