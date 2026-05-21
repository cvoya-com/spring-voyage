// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

/// <summary>
/// Outcome of a routing decision recorded on the activity stream.
/// Routing decisions are first-class evidence.
/// </summary>
public enum RoutingDecisionStatus
{
    /// <summary>
    /// The platform accepted the runtime's tool invocation but the
    /// resulting message has not yet been routed (e.g. queued for
    /// delivery).
    /// </summary>
    Accepted,

    /// <summary>
    /// The platform routed the resulting message(s) to the targeted
    /// addressable(s).
    /// </summary>
    Routed,

    /// <summary>
    /// The routing decision could not complete — e.g. an unknown
    /// target address, a child rejecting the message, or a transport
    /// failure. The associated <see cref="RoutingDecision.Reason"/>
    /// carries the runtime's rationale; failure detail is captured in
    /// the activity stream.
    /// </summary>
    Failed,

    /// <summary>
    /// The runtime <i>decided</i> to route work but the decision was
    /// never executed — the messaging tool was unavailable in the
    /// runtime's tool surface, the model failed to invoke it, or the
    /// invocation was rejected before any delivery attempt (issue
    /// #2581). Distinct from <see cref="Failed"/>, which is a delivery
    /// that was attempted and failed. Self-reported by the runtime via
    /// the <c>sv.runtime.report_decision</c> tool; the
    /// <see cref="RoutingDecision.Metadata"/> carries the
    /// machine-readable not-executed reason.
    /// </summary>
    NotExecuted
}
