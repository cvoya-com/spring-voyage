// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Configuration;

using System.Collections.Generic;

/// <summary>
/// One row in a <see cref="SubsystemConfigurationReport"/> — the result of
/// evaluating a single <see cref="IConfigurationRequirement"/>, flattened for
/// JSON serialisation.
/// </summary>
/// <remarks>
/// The report is exposed as-is over <c>GET /api/v1/system/configuration</c>,
/// so this type doubles as the JSON contract for portal + CLI. Keep the
/// field set stable; add new fields only at the end and default them.
/// </remarks>
/// <param name="RequirementId">Stable id that identifies the requirement — from <see cref="IConfigurationRequirement.RequirementId"/>.</param>
/// <param name="DisplayName">Short human-readable label — from <see cref="IConfigurationRequirement.DisplayName"/>.</param>
/// <param name="Description">Plain-language description of the setting — from <see cref="IConfigurationRequirement.Description"/>.</param>
/// <param name="IsMandatory">Whether the host refuses to start if this requirement is invalid.</param>
/// <param name="Status">The validator's <see cref="ConfigurationStatus"/>.</param>
/// <param name="Severity">Advisory severity the portal / CLI renders.</param>
/// <param name="Reason">Short description of the outcome; may be <c>null</c> when fully met.</param>
/// <param name="Suggestion">Actionable next step for operators; usually populated on non-met results.</param>
/// <param name="EnvironmentVariableNames">Env-var names operators can set to configure the requirement.</param>
/// <param name="ConfigurationSectionPath">The <c>appsettings.json</c> section path, or <c>null</c>.</param>
/// <param name="DocumentationUrl">Absolute-URL-or-relative-path link to the docs page; <c>null</c> when no docs anchor exists.</param>
public sealed record RequirementStatus(
    string RequirementId,
    string DisplayName,
    string Description,
    bool IsMandatory,
    ConfigurationStatus Status,
    SeverityLevel Severity,
    string? Reason,
    string? Suggestion,
    IReadOnlyList<string> EnvironmentVariableNames,
    string? ConfigurationSectionPath,
    string? DocumentationUrl);