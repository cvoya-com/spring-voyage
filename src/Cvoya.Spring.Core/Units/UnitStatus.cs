// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

/// <summary>
/// Represents the lifecycle status of a unit.
/// </summary>
public enum UnitStatus
{
    /// <summary>The unit has been created but its configuration has not yet been finalized.</summary>
    Draft,

    /// <summary>The unit is configured and idle; its runtime container is not running.</summary>
    Stopped,

    /// <summary>The unit is transitioning from stopped to running; the container is being launched.</summary>
    Starting,

    /// <summary>The unit's runtime container is running and the unit is accepting work.</summary>
    Running,

    /// <summary>The unit is transitioning from running to stopped; the container is being torn down.</summary>
    Stopping,

    /// <summary>The unit encountered an unrecoverable error during a lifecycle transition and requires operator attention.</summary>
    Error,

    /// <summary>
    /// The unit is executing backend validation probes inside its chosen container image —
    /// image-pull / start, baseline tool presence, credential acceptance, and (where declared)
    /// model resolution. A Dapr workflow owns the probe run and reports back on completion;
    /// the state is terminal-ish in that it transitions only to <see cref="Stopped"/> on a
    /// successful probe or to <see cref="Error"/> on a failed probe. The step that failed and
    /// a structured error code are persisted on the unit definition
    /// (<c>LastValidationErrorJson</c>), not on the enum.
    /// </summary>
    /// <remarks>
    /// Intentionally appended to the end of the enum. The actor-remoting wire format used by
    /// Dapr serializes this enum by ordinal (System.Text.Json default), so inserting a new
    /// value mid-sequence would shift every downstream ordinal and break compatibility with
    /// any in-flight call or persisted state that predates the upgrade.
    /// </remarks>
    Validating,
}