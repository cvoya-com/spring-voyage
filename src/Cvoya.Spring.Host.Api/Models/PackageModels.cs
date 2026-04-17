// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Models;

using System.Collections.Generic;

/// <summary>
/// Summary entry returned by <c>GET /api/v1/packages</c>. One row per
/// discovered package — the name, an optional description pulled from the
/// package README (when present), and the counts of each content type
/// the package contributes. Counts let the portal's card grid render a
/// meaningful preview without a second round-trip.
/// </summary>
/// <param name="Name">The package's directory name (also its stable id).</param>
/// <param name="Description">Optional short description from the package's <c>README.md</c>.</param>
/// <param name="UnitTemplateCount">Number of unit templates under <c>units/</c>.</param>
/// <param name="AgentTemplateCount">Number of agent templates under <c>agents/</c>.</param>
/// <param name="SkillCount">Number of skills under <c>skills/</c>.</param>
/// <param name="ConnectorCount">Number of connector assets under <c>connectors/</c>.</param>
/// <param name="WorkflowCount">Number of workflow bundles under <c>workflows/</c>.</param>
public record PackageSummary(
    string Name,
    string? Description,
    int UnitTemplateCount,
    int AgentTemplateCount,
    int SkillCount,
    int ConnectorCount,
    int WorkflowCount);

/// <summary>
/// Detail response for <c>GET /api/v1/packages/{name}</c>. Carries every
/// content list the summary only counts, so the portal's detail page can
/// render templates / agents / skills without additional fetches. The
/// Phase-6 install flow will add a <c>version</c> field here (#417); the
/// browse-only shape leaves it off today so the contract stays forward
/// compatible (an absent field is a valid null on every consumer).
/// </summary>
/// <param name="Name">The package name.</param>
/// <param name="Description">Optional description from the package README.</param>
/// <param name="UnitTemplates">Unit templates offered by the package.</param>
/// <param name="AgentTemplates">Agent templates offered by the package.</param>
/// <param name="Skills">Skill bundles offered by the package.</param>
/// <param name="Connectors">Connector assets shipped with the package.</param>
/// <param name="Workflows">Workflow bundles shipped with the package.</param>
public record PackageDetail(
    string Name,
    string? Description,
    IReadOnlyList<UnitTemplateSummary> UnitTemplates,
    IReadOnlyList<AgentTemplateSummary> AgentTemplates,
    IReadOnlyList<SkillSummary> Skills,
    IReadOnlyList<ConnectorSummary> Connectors,
    IReadOnlyList<WorkflowSummary> Workflows);

/// <summary>
/// A single agent template declared by a package. The YAML under
/// <c>packages/{package}/agents/{name}.yaml</c> uses an <c>agent:</c>
/// root with id / name / role / capabilities — we surface the id as
/// the stable name, fall back to the filename when the manifest omits
/// it, and carry the display name and a truncated instructions snippet
/// for the detail card.
/// </summary>
/// <param name="Package">The owning package name.</param>
/// <param name="Name">The agent identifier (file basename or manifest id).</param>
/// <param name="DisplayName">Optional human-readable display name.</param>
/// <param name="Role">Optional role tag.</param>
/// <param name="Description">Optional short description extracted from instructions.</param>
/// <param name="Path">Repo-relative path to the manifest, for display.</param>
public record AgentTemplateSummary(
    string Package,
    string Name,
    string? DisplayName,
    string? Role,
    string? Description,
    string Path);

/// <summary>
/// A skill bundle — the markdown prompt fragment plus an optional
/// tools-manifest sibling.
/// </summary>
/// <param name="Package">The owning package name.</param>
/// <param name="Name">The skill's basename (matches the <c>package.skill</c> reference used by manifests).</param>
/// <param name="HasTools">True when a <c>{name}.tools.json</c> sibling exists.</param>
/// <param name="Path">Repo-relative path to the markdown file.</param>
public record SkillSummary(
    string Package,
    string Name,
    bool HasTools,
    string Path);

/// <summary>
/// A connector asset shipped inside a package. Package connectors
/// augment the platform connector registry but remain discoverable via
/// browse even when no compiled assembly is loaded.
/// </summary>
/// <param name="Package">The owning package name.</param>
/// <param name="Name">The connector asset's basename.</param>
/// <param name="Path">Repo-relative path to the asset.</param>
public record ConnectorSummary(
    string Package,
    string Name,
    string Path);

/// <summary>
/// A workflow bundle shipped inside a package (e.g. a Dockerfile +
/// associated manifests for a multi-step flow).
/// </summary>
/// <param name="Package">The owning package name.</param>
/// <param name="Name">The workflow's directory name.</param>
/// <param name="Path">Repo-relative path to the workflow root.</param>
public record WorkflowSummary(
    string Package,
    string Name,
    string Path);

/// <summary>
/// Response body for <c>GET /api/v1/packages/{package}/templates/{name}</c>.
/// Carries the template manifest's raw YAML so the portal's detail page
/// can render the exact text a user would <c>spring apply</c>. The
/// corresponding CLI verb (<c>spring template show &lt;package&gt;/&lt;name&gt;</c>)
/// rides the same endpoint.
/// </summary>
/// <param name="Package">The owning package name.</param>
/// <param name="Name">The template's unit name.</param>
/// <param name="Path">Repo-relative path to the YAML file.</param>
/// <param name="Yaml">Raw YAML text.</param>
public record UnitTemplateDetail(
    string Package,
    string Name,
    string Path,
    string Yaml);