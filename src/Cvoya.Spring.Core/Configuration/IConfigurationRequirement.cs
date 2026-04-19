// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Configuration;

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Declarative description of a single platform-deploy configuration requirement
/// (tier-1 — environment variable, <c>appsettings.json</c> key, or mounted file).
/// Each subsystem registers its own requirements via
/// <c>services.AddSingleton&lt;IConfigurationRequirement, ...&gt;()</c> inside the
/// <c>AddCvoyaSpring*</c> extension method. The startup validator enumerates
/// every registered requirement, calls <see cref="ValidateAsync"/>, and builds
/// a <see cref="ConfigurationReport"/> shared by
/// <c>GET /api/v1/system/configuration</c>, <c>spring system configuration</c>,
/// and the <c>/system/configuration</c> portal page.
/// </summary>
/// <remarks>
/// <para>
/// <b>Scope.</b> Tier-1 only: platform identity, infrastructure bindings, service
/// wiring. Tier-2 tenant-default credentials (LLM API keys) live behind the
/// <c>ILlmCredentialResolver</c> / <c>ISecretResolver</c> chain and must NOT be
/// expressed as <see cref="IConfigurationRequirement"/> — mixing the two
/// dilutes both. See <c>docs/architecture/configuration.md</c> for the
/// boundary rationale.
/// </para>
/// <para>
/// <b>Validation policy.</b> The validator caches the result at startup and
/// never re-runs <see cref="ValidateAsync"/> during the host lifetime.
/// Expensive probes (HTTP health checks, file reads) are therefore paid once
/// per boot. If an operator changes a tier-1 value they restart the host.
/// </para>
/// <para>
/// <b>Extension seam.</b> The private cloud repo and test harnesses can
/// pre-register alternative requirements (<c>services.AddSingleton</c> before
/// <c>AddCvoyaSpring*()</c>) or substitute a tenant-scoped validator by
/// pre-registering <c>IStartupConfigurationValidator</c>. See
/// <c>AGENTS.md §</c> "Open-Source Platform &amp; Extensibility".
/// </para>
/// </remarks>
public interface IConfigurationRequirement
{
    /// <summary>
    /// Stable machine-readable identifier (kebab-case, e.g.
    /// <c>"github-app-credentials"</c>). Used by CLI drill-downs and the
    /// portal URL fragment — changing it breaks deep links.
    /// </summary>
    string RequirementId { get; }

    /// <summary>
    /// Human-readable label rendered in the portal / CLI. Kept short (a few
    /// words); detail goes into <see cref="Description"/>.
    /// </summary>
    string DisplayName { get; }

    /// <summary>
    /// Subsystem this requirement belongs to (e.g. <c>"Database"</c>,
    /// <c>"GitHub Connector"</c>, <c>"Ollama"</c>). The validator groups
    /// requirements by this key in the <see cref="ConfigurationReport"/>.
    /// </summary>
    string SubsystemName { get; }

    /// <summary>
    /// When <c>true</c>, a <see cref="ConfigurationStatus.Invalid"/> status
    /// fails host startup — the validator's hosted service throws the
    /// <see cref="ConfigurationRequirementStatus.FatalError"/> from
    /// <c>StartAsync</c>. When <c>false</c>, the subsystem is optional:
    /// validation failures surface in the report but the host keeps booting
    /// and dependent features register themselves as disabled.
    /// </summary>
    bool IsMandatory { get; }

    /// <summary>
    /// Environment-variable names the operator should read / set. Surfaced
    /// by the report so operators can eyeball which var to tweak without
    /// cross-referencing documentation. Empty when the requirement is not
    /// addressable via an env var (e.g. a mounted file path).
    /// </summary>
    IReadOnlyList<string> EnvironmentVariableNames { get; }

    /// <summary>
    /// Configuration section path (colon-delimited, e.g.
    /// <c>"ConnectionStrings:SpringDb"</c> or <c>"GitHub:AppId"</c>). Equivalent
    /// to the env-var shape but for <c>appsettings.json</c>. <c>null</c> when
    /// the requirement isn't a direct configuration-section binding.
    /// </summary>
    string? ConfigurationSectionPath { get; }

    /// <summary>
    /// Plain-language description of what the setting does — surfaced in the
    /// portal card and the CLI table. This is "what operators need to know",
    /// not "what the code does internally".
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Optional docs link rendered alongside the requirement. Prefer
    /// per-requirement anchors on <c>docs/guide/deployment.md</c> or
    /// <c>docs/architecture/configuration.md</c>.
    /// </summary>
    Uri? DocumentationUrl { get; }

    /// <summary>
    /// Evaluate the current state of this requirement and return a
    /// <see cref="ConfigurationRequirementStatus"/>. Called once, at host
    /// startup, by the registered <c>IStartupConfigurationValidator</c>.
    /// </summary>
    /// <param name="cancellationToken">Host startup cancellation token.</param>
    /// <returns>The validator result for this requirement.</returns>
    Task<ConfigurationRequirementStatus> ValidateAsync(CancellationToken cancellationToken);
}