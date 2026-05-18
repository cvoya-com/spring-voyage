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
/// <param name="HumanTemplateCount">
/// Number of <c>HumanTemplate</c> bundles under <c>templates/</c> (ADR-0046 §4).
/// </param>
public record PackageSummary(
    string Name,
    string? Description,
    int UnitTemplateCount,
    int AgentTemplateCount,
    int SkillCount,
    int HumanTemplateCount,
    string? Version = null);

/// <summary>
/// Detail response for <c>GET /api/v1/packages/{name}</c>. Carries every
/// content list the summary only counts, so the portal's detail page can
/// render templates / agents / skills without additional fetches.
/// </summary>
/// <param name="Name">The package name.</param>
/// <param name="Description">Optional description from the package README.</param>
/// <param name="Readme">Full README.md content in raw Markdown, when present.</param>
/// <param name="Version">
/// Package version (ADR-0037 D5). Opaque string. Two packages with the
/// same <see cref="Name"/> and different <see cref="Version"/> values
/// may be installed in the same tenant simultaneously.
/// </param>
/// <param name="UnitTemplates">Unit templates offered by the package.</param>
/// <param name="AgentTemplates">Agent templates offered by the package.</param>
/// <param name="Skills">Skill bundles offered by the package.</param>
/// <param name="HumanTemplates">
/// <c>HumanTemplate</c> bundles shipped with the package (ADR-0046 §4).
/// Stamped via <c>- human: { from: &lt;template-name&gt; }</c> on a unit's
/// <c>members:</c> list.
/// </param>
/// <param name="ConnectorDeclarations">
/// Connector slugs the package effectively requires (ADR-0037 D3). Each
/// entry is one slug from the union of every contained artefact's
/// <c>requires:</c> block — the wizard / CLI render the
/// connector-binding step from this list.
/// </param>
/// <param name="Content">
/// Top-level artefacts declared in the manifest's <c>content:</c> list.
/// Each entry carries the artefact discriminator (<c>unit</c>,
/// <c>agent</c>, <c>skill</c>) and the reference value the manifest
/// declares.
/// </param>
/// <param name="Execution">
/// Package-level <c>execution:</c> declaration (#1679), or null when
/// the package author declared no <c>execution:</c> block. Surfaces the
/// inheritable defaults (<c>image</c>, <c>runtime</c>, <c>provider</c>,
/// <c>model</c>) and the optional <c>inherit:</c> selector so the
/// wizard / CLI can render which member units pick up which defaults
/// before the operator hits install.
/// </param>
public record PackageDetail(
    string Name,
    string? Description,
    string? Readme,
    string? Version,
    IReadOnlyList<UnitTemplateSummary> UnitTemplates,
    IReadOnlyList<AgentTemplateSummary> AgentTemplates,
    IReadOnlyList<SkillSummary> Skills,
    IReadOnlyList<HumanTemplateSummary> HumanTemplates,
    IReadOnlyList<RequiredConnectorSummary> ConnectorDeclarations,
    IReadOnlyList<PackageContentEntry> Content,
    PackageExecutionSummary? Execution = null);

/// <summary>
/// Wire shape for the package-level <c>execution:</c> block (#1679).
/// Mirrors <see cref="Cvoya.Spring.Manifest.PackageExecutionDeclaration"/>
/// so the portal / CLI can read the package's inheritable execution
/// defaults — and the optional <c>inherit:</c> selector — before
/// kicking off install.
/// </summary>
/// <param name="Image">Default container image inherited by member units.</param>
/// <param name="Provider">Default LLM provider inherited by member units.</param>
/// <param name="Model">Default model identifier inherited by member units.</param>
/// <param name="InheritUnits">
/// <c>null</c> when every member inherits (the default and the
/// <c>inherit: all</c> spelling); otherwise the explicit list of unit
/// names that participate.
/// </param>
public record PackageExecutionSummary(
    string? Image,
    string? Provider,
    string? Model,
    IReadOnlyList<string>? InheritUnits);

/// <summary>
/// Wire-shape for one entry in <see cref="PackageDetail.Content"/> —
/// the parsed <c>content:</c> list on the package manifest (#1718 item 2).
/// </summary>
/// <param name="Kind">
/// Artefact discriminator: <c>unit</c>, <c>agent</c>, <c>skill</c>, or
/// <c>workflow</c>. Lower-cased to match the YAML key the manifest
/// declares.
/// </param>
/// <param name="Name">
/// The reference string declared in the manifest. For inline bodies
/// this is the inline artefact's <c>id</c> / <c>name</c>; for bare
/// references it's the bare name (<c>my-unit</c>); for cross-package
/// references it's the qualified <c>pkg/name</c> form.
/// </param>
public record PackageContentEntry(
    string Kind,
    string Name);

/// <summary>
/// Wire shape for one entry in <see cref="PackageDetail.ConnectorDeclarations"/>
/// — one connector slug from the union of every artefact's
/// <c>requires:</c> block (ADR-0037 D3). Surfaces the slug so the wizard
/// / CLI can render a connector-binding form for it at install time.
/// </summary>
/// <param name="Type">The connector slug (matches <c>IConnectorType.Slug</c>).</param>
/// <param name="Required">
/// Always true under ADR-0037 D3 — every declared requirement is
/// required. Kept on the wire shape for forward compatibility when
/// optional requirements (e.g. capability-typed) land.
/// </param>
public record RequiredConnectorSummary(
    string Type,
    bool Required);

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
/// A <c>HumanTemplate</c> shipped inside a package (ADR-0046 §4). One
/// row per <c>./templates/&lt;name&gt;/package.yaml</c> document with
/// <c>kind: HumanTemplate</c>.
/// </summary>
/// <param name="Package">The owning package name.</param>
/// <param name="Name">The template's name (matches the containing folder).</param>
/// <param name="DisplayName">Optional human-readable display name.</param>
/// <param name="Description">Optional short description.</param>
/// <param name="Path">Repo-relative path to the manifest, for display.</param>
public record HumanTemplateSummary(
    string Package,
    string Name,
    string? DisplayName,
    string? Description,
    string Path);

/// <summary>
/// Response body for <c>GET /api/v1/tenant/packages/{name}/required-credentials</c>
/// (#2181). Lets the install wizard render credential inputs for any
/// <c>(provider, authMethod)</c> edge a member unit consumes — without
/// the operator having to hit Install first and recover from
/// <c>CredentialsMissing</c>. Computed by the same
/// <see cref="Services.CredentialBindingResolver"/> the install
/// pre-flight uses, so the two surfaces never disagree.
/// </summary>
/// <param name="Required">
/// One entry per distinct <c>(provider, authMethod)</c> edge consumed
/// by a member unit. Empty when the package's units have no LLM
/// requirement (e.g. only Ollama-backed units).
/// </param>
public record PackageRequiredCredentialsResponse(
    IReadOnlyList<PackageRequiredCredentialEntryResponse> Required);

/// <summary>
/// One required-credential edge surfaced by
/// <see cref="PackageRequiredCredentialsResponse"/>.
/// </summary>
/// <param name="Provider">Provider id — <c>anthropic</c>, <c>openai</c>, …</param>
/// <param name="AuthMethod">Auth method on the edge — <c>oauth</c> or <c>api-key</c>.</param>
/// <param name="SecretName">Canonical secret name the resolver looks for at install time.</param>
/// <param name="CredentialEnvVar">Env var the runtime launcher reads the resolved value from.</param>
/// <param name="ConsumingUnits">Member units whose runtime/provider edge consumes this credential.</param>
public record PackageRequiredCredentialEntryResponse(
    string Provider,
    string AuthMethod,
    string SecretName,
    string CredentialEnvVar,
    IReadOnlyList<string> ConsumingUnits);

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
