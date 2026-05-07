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
/// <see cref="Cvoya.Spring.Core.Observability.WaitTimeEntry"/>. Durations are
/// derived from paired <c>StateChanged</c> lifecycle transitions (#476); the
/// <c>StateTransitions</c> counter reports every <c>StateChanged</c> event in
/// the window.
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

/// <summary>
/// One bucket of cost data in a per-agent or per-unit time-series.
/// </summary>
/// <param name="T">Inclusive UTC start of the bucket.</param>
/// <param name="CostUsd">Total cost (USD) accumulated inside <c>[T, T + bucket)</c>. Always emitted — zero for empty buckets.</param>
public record AnalyticsCostTimeseriesBucketResponse(DateTimeOffset T, decimal CostUsd);

/// <summary>
/// Response body for <c>GET /api/v1/tenant/analytics/agents/{id}/cost-timeseries</c>
/// and <c>GET /api/v1/tenant/analytics/units/{id}/cost-timeseries</c>. A
/// zero-filled cost time-series for a single agent or unit (#569). The
/// <paramref name="Scope"/> field (<c>agents</c> or <c>units</c>) and
/// <paramref name="Id"/> echo the request parameters so callers can route
/// multiple concurrent fetches back to the right detail page without
/// re-parsing the request URL.
/// </summary>
/// <param name="Scope">Either <c>agents</c> or <c>units</c>.</param>
/// <param name="Id">The agent or unit identifier.</param>
/// <param name="Bucket">Canonical bucket label (<c>"1h"</c>, <c>"1d"</c>, <c>"7d"</c>).</param>
/// <param name="From">Inclusive UTC start of the window.</param>
/// <param name="To">Exclusive UTC end of the window.</param>
/// <param name="Points">Ordered bucket list; <c>Points[0].T == From</c>.</param>
public record AnalyticsCostTimeseriesResponse(
    string Scope,
    string Id,
    string Bucket,
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<AnalyticsCostTimeseriesBucketResponse> Points);