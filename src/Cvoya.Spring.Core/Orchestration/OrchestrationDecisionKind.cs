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
    /// The runtime forwarded the inbound message to a single target via
    /// <c>delegate_to</c>.
    /// </summary>
    Delegate,

    /// <summary>
    /// The runtime forwarded the inbound message to multiple targets
    /// in parallel via <c>fanout_to</c>.
    /// </summary>
    Fanout
}
