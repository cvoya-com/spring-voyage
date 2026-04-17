// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Observability;

/// <summary>
/// Wait-time rollup for a single source address (agent or unit) — see #457,
/// Analytics Wait times tab. Durations are computed by pairing consecutive
/// <c>StateChanged</c> activity events per source and accumulating time-in-state
/// into the three exposed buckets (#476); the Rx activity pipeline (#391) is
/// the signal source.
/// </summary>
/// <param name="Source">
/// Wire-format source address (<c>agent://name</c>, <c>unit://name</c>).
/// </param>
/// <param name="IdleSeconds">
/// Seconds spent in the <c>Idle</c> state (waiting for input) over the window.
/// </param>
/// <param name="BusySeconds">
/// Seconds spent in the <c>Active</c> state (executing work) over the window.
/// </param>
/// <param name="WaitingForHumanSeconds">
/// Seconds spent in the <c>Paused</c> state (awaiting a human response) over
/// the window.
/// </param>
/// <param name="StateTransitions">
/// Raw count of <c>StateChanged</c> events observed in the window. Includes
/// both canonical lifecycle transitions (which feed the duration buckets
/// above) and metadata-edit events (which don't).
/// </param>
public record WaitTimeEntry(
    string Source,
    double IdleSeconds,
    double BusySeconds,
    double WaitingForHumanSeconds,
    long StateTransitions);

/// <summary>
/// Collection of wait-time entries in a time range (#457, Analytics Wait times
/// tab). See <see cref="WaitTimeEntry"/> for the per-entry contract.
/// </summary>
/// <param name="Entries">Per-source wait-time entries.</param>
/// <param name="From">The start of the rollup time range.</param>
/// <param name="To">The end of the rollup time range.</param>
public record WaitTimeRollup(
    IReadOnlyList<WaitTimeEntry> Entries,
    DateTimeOffset From,
    DateTimeOffset To);