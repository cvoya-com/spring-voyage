// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Observability;

/// <summary>
/// Wait-time rollup for a single source address (agent or unit) — see #457,
/// Analytics Wait times tab. Today this is derived from the count of
/// <c>StateChanged</c> activity events; once PR-PLAT-OBS-1 (#391) lands, the
/// same shape is populated with real duration metrics without a contract
/// change.
/// </summary>
/// <param name="Source">
/// Wire-format source address (<c>agent://name</c>, <c>unit://name</c>).
/// </param>
/// <param name="IdleSeconds">Seconds spent idle (waiting for input).</param>
/// <param name="BusySeconds">Seconds spent executing work.</param>
/// <param name="WaitingForHumanSeconds">Seconds spent awaiting a human response.</param>
/// <param name="StateTransitions">
/// Raw count of <c>StateChanged</c> events observed in the window. The durations
/// above are zero until PR-PLAT-OBS-1 supplies start/end timestamps; this
/// counter is the fallback signal the CLI and portal can render meanwhile.
/// </param>
public record WaitTimeEntry(
    string Source,
    double IdleSeconds,
    double BusySeconds,
    double WaitingForHumanSeconds,
    long StateTransitions);

/// <summary>
/// Collection of wait-time entries in a time range (#457, Analytics Wait times
/// tab). See <see cref="WaitTimeEntry"/> for the placeholder-until-observability
/// caveat.
/// </summary>
/// <param name="Entries">Per-source wait-time entries.</param>
/// <param name="From">The start of the rollup time range.</param>
/// <param name="To">The end of the rollup time range.</param>
public record WaitTimeRollup(
    IReadOnlyList<WaitTimeEntry> Entries,
    DateTimeOffset From,
    DateTimeOffset To);