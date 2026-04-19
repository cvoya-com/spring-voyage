// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Configuration;

using System;
using System.Collections.Generic;

/// <summary>
/// Top-level payload produced by the startup configuration validator. Cached
/// for the lifetime of the host and served over
/// <c>GET /api/v1/system/configuration</c>, <c>spring system configuration</c>,
/// and the <c>/system/configuration</c> portal page.
/// </summary>
/// <param name="Status">Overall <see cref="ConfigurationReportStatus"/> across every subsystem.</param>
/// <param name="GeneratedAt">When the validator ran. Cached-at-startup: this timestamp does not move until the host restarts.</param>
/// <param name="Subsystems">Per-subsystem grouping. Ordered by subsystem name for stable rendering.</param>
public sealed record ConfigurationReport(
    ConfigurationReportStatus Status,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<SubsystemConfigurationReport> Subsystems)
{
    /// <summary>
    /// Convenience: an empty "no requirements registered yet" report. Used
    /// by hosts that query the cache before the validator has run (should
    /// be rare — the validator is registered as the first hosted service).
    /// </summary>
    public static ConfigurationReport Empty { get; } =
        new(ConfigurationReportStatus.Healthy, DateTimeOffset.MinValue, Array.Empty<SubsystemConfigurationReport>());
}