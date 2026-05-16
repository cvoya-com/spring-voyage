// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Lifecycle;

using Cvoya.Spring.Core.Artefacts;

/// <summary>
/// Shared lifecycle status for any artefact whose runtime is a container —
/// today: <c>Unit</c> and <c>Agent</c> (see <see cref="ArtefactKind"/>). Both
/// kinds run the same container runtimes and the same execution model
/// (image, runtime catalogue entry, model, credentials), so they share the
/// same state machine (#2364).
/// </summary>
/// <remarks>
/// Replaces the historical <c>UnitStatus</c> and
/// <c>AgentLifecycleStatus.Active/Error</c> per the v0.1 aggressive-cleanup
/// decision in #2359 / #2364 — no back-compat shim.
///
/// Enum values are positional in actor-remoting wire format (System.Text.Json
/// default). The order here MATCHES the historical <c>UnitStatus</c> ordering
/// so persisted unit state survives the rename without an ordinal shift.
/// Appending new values to the end is safe; reordering or inserting in the
/// middle is not.
/// </remarks>
public enum LifecycleStatus
{
    /// <summary>The artefact has been created but its configuration has not yet been finalized.</summary>
    Draft,

    /// <summary>The artefact is configured and idle; its runtime container is not running.</summary>
    Stopped,

    /// <summary>The artefact is transitioning from stopped to running; the container is being launched.</summary>
    Starting,

    /// <summary>The artefact's runtime container is running and the artefact is accepting work.</summary>
    Running,

    /// <summary>The artefact is transitioning from running to stopped; the container is being torn down.</summary>
    Stopping,

    /// <summary>The artefact encountered an unrecoverable error during a lifecycle transition and requires operator attention.</summary>
    Error,

    /// <summary>
    /// The artefact is executing backend validation probes inside its chosen container image —
    /// image-pull / start, baseline tool presence, credential acceptance, and (where declared)
    /// model resolution. A Dapr workflow owns the probe run and reports back on completion;
    /// the state is terminal-ish in that it transitions only to <see cref="Stopped"/> on a
    /// successful probe or to <see cref="Error"/> on a failed probe.
    /// </summary>
    Validating,
}
