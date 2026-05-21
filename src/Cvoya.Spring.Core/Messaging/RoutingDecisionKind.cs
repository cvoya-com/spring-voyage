// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

/// <summary>
/// Identifies the kind of routing decision recorded when a runtime logs a
/// decision via <c>sv.runtime.report_decision</c>. See ADR-0039 § 4
/// (routing decisions are first-class evidence).
/// </summary>
public enum RoutingDecisionKind
{
    /// <summary>
    /// The runtime decided to route the work to a single target.
    /// </summary>
    Delegate,

    /// <summary>
    /// The runtime decided to route the work to multiple targets.
    /// </summary>
    Fanout
}
