// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Observability;

/// <summary>
/// Counts message / event throughput grouped by source address within a time
/// window (#457, Analytics Throughput tab). One entry per source.
/// </summary>
/// <param name="Source">
/// The wire-format source address (<c>agent://name</c>, <c>unit://name</c>).
/// Scripts filter by scheme prefix; the server emits whatever is present in
/// the underlying activity stream so new schemes show up without a code change.
/// </param>
/// <param name="MessagesReceived">Count of <c>MessageReceived</c> events.</param>
/// <param name="MessagesSent">Count of <c>MessageSent</c> events.</param>
/// <param name="Turns">Count of <c>ConversationStarted</c> events (one per turn-initiating interaction).</param>
/// <param name="ToolCalls">Count of <c>DecisionMade</c> events (a proxy for tool-call decisions until a dedicated event type lands).</param>
public record ThroughputEntry(
    string Source,
    long MessagesReceived,
    long MessagesSent,
    long Turns,
    long ToolCalls);

/// <summary>
/// Rollup of throughput counters over a time range (#457, Analytics Throughput
/// tab). Surfaces per-source entries plus tenant-wide totals so the CLI and
/// portal can render either view without re-aggregating.
/// </summary>
/// <param name="Entries">Throughput counters broken down by source.</param>
/// <param name="From">The start of the rollup time range.</param>
/// <param name="To">The end of the rollup time range.</param>
public record ThroughputRollup(
    IReadOnlyList<ThroughputEntry> Entries,
    DateTimeOffset From,
    DateTimeOffset To);