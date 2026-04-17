// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

/// <summary>
/// Response body for <c>GET /api/v1/analytics/throughput</c>. Mirrors
/// <see cref="Cvoya.Spring.Core.Observability.ThroughputRollup"/> but keeps
/// the HTTP surface DTO-shaped so OpenAPI / Kiota can round-trip stable
/// property names.
/// </summary>
/// <param name="Source">
/// Wire-format source address (<c>agent://name</c>, <c>unit://name</c>).
/// </param>
/// <param name="MessagesReceived">Count of <c>MessageReceived</c> events.</param>
/// <param name="MessagesSent">Count of <c>MessageSent</c> events.</param>
/// <param name="Turns">Count of <c>ConversationStarted</c> events.</param>
/// <param name="ToolCalls">Count of <c>DecisionMade</c> events (proxy for tool calls).</param>
public record ThroughputEntryResponse(
    string Source,
    long MessagesReceived,
    long MessagesSent,
    long Turns,
    long ToolCalls);

/// <summary>Response body for <c>GET /api/v1/analytics/throughput</c>.</summary>
/// <param name="Entries">Throughput counters broken down by source.</param>
/// <param name="From">Start of the rollup window.</param>
/// <param name="To">End of the rollup window.</param>
public record ThroughputRollupResponse(
    IReadOnlyList<ThroughputEntryResponse> Entries,
    DateTimeOffset From,
    DateTimeOffset To);

/// <summary>
/// Response body for <c>GET /api/v1/analytics/waits</c>. Mirrors
/// <see cref="Cvoya.Spring.Core.Observability.WaitTimeEntry"/>. Duration
/// fields are zero-filled until PR-PLAT-OBS-1 (#391) lands; the
/// <c>StateTransitions</c> counter is the placeholder signal.
/// </summary>
/// <param name="Source">Wire-format source address.</param>
/// <param name="IdleSeconds">Seconds spent idle.</param>
/// <param name="BusySeconds">Seconds spent executing work.</param>
/// <param name="WaitingForHumanSeconds">Seconds waiting for a human response.</param>
/// <param name="StateTransitions">Count of <c>StateChanged</c> events observed in the window.</param>
public record WaitTimeEntryResponse(
    string Source,
    double IdleSeconds,
    double BusySeconds,
    double WaitingForHumanSeconds,
    long StateTransitions);

/// <summary>Response body for <c>GET /api/v1/analytics/waits</c>.</summary>
/// <param name="Entries">Per-source wait-time entries.</param>
/// <param name="From">Start of the rollup window.</param>
/// <param name="To">End of the rollup window.</param>
public record WaitTimeRollupResponse(
    IReadOnlyList<WaitTimeEntryResponse> Entries,
    DateTimeOffset From,
    DateTimeOffset To);