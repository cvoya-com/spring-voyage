// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Configuration;

using System.Collections.Generic;

/// <summary>
/// Per-subsystem section of the <see cref="ConfigurationReport"/>. Each
/// registered <see cref="IConfigurationRequirement"/> contributes one
/// <see cref="RequirementStatus"/> row; requirements are grouped by their
/// <see cref="IConfigurationRequirement.SubsystemName"/>.
/// </summary>
/// <param name="SubsystemName">Human-readable subsystem label (e.g. <c>"Database"</c>, <c>"GitHub Connector"</c>).</param>
/// <param name="Status">Aggregated status across every requirement in the subsystem.</param>
/// <param name="Requirements">The requirement-level rows, ordered by registration.</param>
public sealed record SubsystemConfigurationReport(
    string SubsystemName,
    ConfigurationReportStatus Status,
    IReadOnlyList<RequirementStatus> Requirements);