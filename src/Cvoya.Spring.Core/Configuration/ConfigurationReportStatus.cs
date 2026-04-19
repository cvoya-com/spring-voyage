// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Configuration;

/// <summary>
/// Top-level summary state of the <see cref="ConfigurationReport"/> (and of
/// each <see cref="SubsystemConfigurationReport"/>). Computed by the validator
/// from the collected <see cref="RequirementStatus"/> entries.
/// </summary>
public enum ConfigurationReportStatus
{
    /// <summary>
    /// Every requirement is <see cref="ConfigurationStatus.Met"/> with
    /// <see cref="SeverityLevel.Information"/>. The platform is fully
    /// configured with no caveats.
    /// </summary>
    Healthy = 0,

    /// <summary>
    /// At least one requirement is <see cref="ConfigurationStatus.Disabled"/>,
    /// or <see cref="ConfigurationStatus.Met"/> with
    /// <see cref="SeverityLevel.Warning"/>. The host booted fine but
    /// optional features are off or degraded. Operators should read the
    /// report to see what's missing.
    /// </summary>
    Degraded = 1,

    /// <summary>
    /// At least one requirement is <see cref="ConfigurationStatus.Invalid"/>.
    /// For mandatory requirements the validator aborts startup before the
    /// report can be read, so <see cref="Failed"/> only appears on the
    /// live report when an optional requirement is misconfigured.
    /// </summary>
    Failed = 2,
}