// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Orchestration;

/// <summary>
/// Outcome of an orchestration decision recorded on the activity stream.
/// See ADR-0039 § 4 ("Orchestration decisions are first-class evidence").
/// </summary>
public enum OrchestrationDecisionStatus
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
    /// The orchestration decision could not complete — e.g. an unknown
    /// target address, a child rejecting the message, or a transport
    /// failure. The associated <see cref="OrchestrationDecision.Reason"/>
    /// carries the runtime's rationale; failure detail is captured in
    /// the activity stream.
    /// </summary>
    Failed
}
