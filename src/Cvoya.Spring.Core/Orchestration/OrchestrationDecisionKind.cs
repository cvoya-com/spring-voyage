// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Orchestration;

/// <summary>
/// Identifies the kind of orchestration decision recorded when a unit's
/// runtime invokes one of the platform-supplied orchestration tools.
/// See ADR-0039 § 4 ("Orchestration decisions are first-class evidence").
/// </summary>
public enum OrchestrationDecisionKind
{
    /// <summary>
    /// The runtime forwarded the inbound message to a single child via
    /// <c>delegate_to_child</c>.
    /// </summary>
    Delegate,

    /// <summary>
    /// The runtime forwarded the inbound message to multiple children
    /// in parallel via <c>fanout_to_children</c>.
    /// </summary>
    Fanout,

    /// <summary>
    /// The runtime invoked an inspection tool (e.g. <c>list_children</c>
    /// or <c>inspect_child</c>) as part of an explicit decision sequence.
    /// </summary>
    Inspect,

    /// <summary>
    /// The runtime considered delegating but elected not to. Recorded so
    /// operators can audit non-delegations as well as delegations.
    /// </summary>
    NoOp
}
