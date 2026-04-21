// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Costs;

/// <summary>
/// A single bucket of aggregated cost within a time-series.
/// </summary>
/// <param name="BucketStart">The inclusive UTC start of the bucket.</param>
/// <param name="Cost">The total cost (USD) for records whose timestamp falls within <c>[BucketStart, BucketStart + BucketSize)</c>.</param>
public record CostTimeseriesBucket(DateTimeOffset BucketStart, decimal Cost);

/// <summary>
/// A tenant-wide cost time-series — zero-filled so consumers (the portal
/// sparkline, analytics time-series charts) can render a continuous line
/// without having to invent missing buckets.
/// </summary>
/// <param name="From">The inclusive UTC start of the requested window.</param>
/// <param name="To">The exclusive UTC end of the requested window.</param>
/// <param name="Bucket">The bucket grain as an ISO-8601-ish duration string (e.g. <c>"1h"</c>, <c>"1d"</c>, <c>"7d"</c>).</param>
/// <param name="Series">The ordered list of buckets. Length equals <c>ceil((To - From) / bucket)</c>.</param>
public record CostTimeseries(
    DateTimeOffset From,
    DateTimeOffset To,
    string Bucket,
    IReadOnlyList<CostTimeseriesBucket> Series);