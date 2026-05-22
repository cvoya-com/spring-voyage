// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;

using YamlDotNet.Serialization;

/// <summary>
/// Typed view of an <c>./units/&lt;name&gt;.yaml</c> document under
/// ADR-0037. Each unit YAML is a kind-discriminated top-level document
/// with its own <c>apiVersion</c>, <c>kind: Unit</c>, <c>name</c>,
/// <c>description</c>, optional <c>readme</c>, optional <c>requires</c>,
/// and unit body fields. The wrapping <c>unit:</c> key from the
/// pre-ADR-0037 grammar is gone.
/// </summary>
public class UnitManifest
{
    /// <summary>API version string (e.g. <c>spring.voyage/v1</c>).</summary>
    [YamlMember(Alias = "apiVersion")]
    public string? ApiVersion { get; set; }

    /// <summary>Document kind. Must be the literal string <c>Unit</c>.</summary>
    [YamlMember(Alias = "kind")]
    public string? Kind { get; set; }

    /// <summary>The unit's unique name / address path.</summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>
    /// Optional human-friendly label for the unit. When set, surfaces as
    /// the unit's <c>DisplayName</c> on the persisted definition and in
    /// every read API / CLI / portal projection; when null or whitespace,
    /// the persistence layer falls back to <see cref="Name"/>. An
    /// install-time operator override (<c>--display-name</c>) still wins
    /// over both fields for the package's top-level activatable.
    /// </summary>
    [YamlMember(Alias = "displayName")]
    public string? DisplayName { get; set; }

    /// <summary>Human-readable single-line description of the unit's purpose.</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>
    /// Optional relative path to a markdown file with long-form prose for
    /// UIs (ADR-0037 decision 2).
    /// </summary>
    [YamlMember(Alias = "readme")]
    public string? Readme { get; set; }

    /// <summary>
    /// Optional template reference (ADR-0043 §5). Bare name resolves
    /// within the package; qualified name <c>&lt;pkg&gt;/&lt;name&gt;@&lt;version&gt;</c>
    /// resolves cross-package per ADR-0037 §5.
    /// </summary>
    [YamlMember(Alias = "from")]
    public string? From { get; set; }

    /// <summary>
    /// Optional AI runtime configuration. Parsed but not yet applied
    /// by the platform API.
    /// </summary>
    [YamlMember(Alias = "ai")]
    public AiManifest? Ai { get; set; }

    /// <summary>
    /// User-provided prompt for the unit. Top-level slot per ADR-0043 + #2298;
    /// replaces the legacy <c>ai.prompt:</c> nesting which is removed in
    /// chunk 2 of the ADR-0043 implementation.
    /// </summary>
    [YamlMember(Alias = "instructions")]
    public string? Instructions { get; set; }

    /// <summary>Members of the unit (agents or other units).</summary>
    [YamlMember(Alias = "members")]
    public List<MemberManifest>? Members { get; set; }

    /// <summary>Execution runtime description. Parsed but not yet applied.</summary>
    [YamlMember(Alias = "execution")]
    public ExecutionManifest? Execution { get; set; }

    /// <summary>
    /// Optional <c>requires:</c> block declaring this unit's own
    /// requirements (ADR-0037 decision 3). Each entry is a single-key
    /// mapping (<c>connector: github</c>, etc.). The package's effective
    /// requirement set is the union of every contained artefact's
    /// <see cref="Requires"/>.
    /// </summary>
    [YamlMember(Alias = "requires")]
    public List<RequirementEntry>? Requires { get; set; }

    /// <summary>Unit-level policies. Parsed but not yet applied.</summary>
    [YamlMember(Alias = "policies")]
    public Dictionary<string, object>? Policies { get; set; }

    /// <summary>
    /// Optional seed own-expertise entries for the unit (#488).
    /// </summary>
    [YamlMember(Alias = "expertise")]
    public List<ExpertiseManifestEntry>? Expertise { get; set; }

    /// <summary>
    /// Optional boundary configuration for the unit (#494).
    /// </summary>
    [YamlMember(Alias = "boundary")]
    public BoundaryManifest? Boundary { get; set; }

    /// <summary>Optional role identifier for multicast resolution.</summary>
    [YamlMember(Alias = "role")]
    public string? Role { get; set; }

    /// <summary>Optional UI color hint (hex or named color).</summary>
    [YamlMember(Alias = "color")]
    public string? Color { get; set; }

    /// <summary>Optional specialty label surfaced for unit selection; the platform does not route on it.</summary>
    [YamlMember(Alias = "specialty")]
    public string? Specialty { get; set; }

    /// <summary>Whether this unit processes inbound messages. Defaults to true.</summary>
    [YamlMember(Alias = "enabled")]
    public bool? Enabled { get; set; }

    /// <summary>How this unit participates in dispatch (auto or onDemand).</summary>
    [YamlMember(Alias = "executionMode")]
    public string? ExecutionMode { get; set; }
}

/// <summary>
/// One entry in a unit / agent manifest <c>expertise:</c> list. The user-
/// facing YAML authoring key is <c>domain:</c> but <c>name:</c> is also
/// accepted so a dump from <c>GET /api/v1/agents/{id}/expertise</c> can be
/// round-tripped back into a definition file.
/// </summary>
public class ExpertiseManifestEntry
{
    /// <summary>The expertise domain name (preferred authoring key).</summary>
    [YamlMember(Alias = "domain")]
    public string? Domain { get; set; }

    /// <summary>
    /// Alias for <see cref="Domain"/>. Accepted so wire-shaped JSON (where
    /// the field is spelled <c>name</c>) can round-trip through a manifest
    /// file without renaming.
    /// </summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>Optional human-readable description of the capability.</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>
    /// Optional proficiency level. Expected values (case-insensitive):
    /// <c>beginner</c>, <c>intermediate</c>, <c>advanced</c>, <c>expert</c>.
    /// Unrecognised values are persisted as-is on the JSON definition and
    /// silently ignored by the seed provider at activation time.
    /// </summary>
    [YamlMember(Alias = "level")]
    public string? Level { get; set; }
}

/// <summary>
/// AI configuration for a unit / agent under ADR-0038. Carries the
/// agent runtime id (<see cref="Runtime"/>) and the structured
/// <c>{provider, id}</c> model selector (<see cref="Model"/>).
/// </summary>
public class AiManifest
{
    /// <summary>
    /// Agent runtime id (ADR-0038): <c>claude-code</c>, <c>codex</c>,
    /// <c>gemini</c>, <c>spring-voyage</c>, or a future custom runtime
    /// declared in <c>eng/runtime-catalog/runtime-catalog.yaml</c>.
    /// </summary>
    [YamlMember(Alias = "runtime")]
    public string? Runtime { get; set; }

    /// <summary>
    /// Structured model selector — <c>{provider, id}</c>. Provider is
    /// intrinsic to the model; there is no separate <c>provider</c>
    /// slot anywhere in the manifest or wire shape.
    /// </summary>
    [YamlMember(Alias = "model")]
    public AiModelManifest? Model { get; set; }

    /// <summary>Skills available to the agent.</summary>
    [YamlMember(Alias = "skills")]
    public List<SkillReference>? Skills { get; set; }

    /// <summary>
    /// Optional execution environment for the agent runtime. The image
    /// declared here is projected onto <c>execution.image</c> at install
    /// time when the YAML does not declare a top-level
    /// <c>execution.image</c>.
    /// </summary>
    [YamlMember(Alias = "environment")]
    public AiEnvironmentManifest? Environment { get; set; }
}

/// <summary>
/// Execution-environment block on <see cref="AiManifest"/>. Carries the
/// container image to launch the agent runtime in; the install activator
/// projects <see cref="Image"/> onto <c>execution.image</c> when a
/// top-level slot is absent.
/// </summary>
public class AiEnvironmentManifest
{
    /// <summary>
    /// Container image reference for the agent runtime
    /// (e.g. <c>ghcr.io/cvoya-com/spring-voyage-claude-code-base:latest</c>).
    /// </summary>
    [YamlMember(Alias = "image")]
    public string? Image { get; set; }
}

/// <summary>
/// Structured model selector under ADR-0038 — the model id together
/// with its hosting provider. Provider is intrinsic to the model.
/// </summary>
/// <remarks>
/// The matching wire JSON form is <c>{ "provider": "...", "id": "..." }</c>.
/// </remarks>
public class AiModelManifest
{
    /// <summary>
    /// Provider id (matches <c>ModelProvider.Id</c> in
    /// <c>eng/runtime-catalog/runtime-catalog.yaml</c>): <c>anthropic</c>,
    /// <c>openai</c>, <c>google</c>, <c>ollama</c>, …
    /// </summary>
    [YamlMember(Alias = "provider")]
    public string? Provider { get; set; }

    /// <summary>
    /// Provider-scoped model id (e.g. <c>claude-opus-4-7</c>,
    /// <c>gpt-4o</c>, <c>llama3.2:3b</c>).
    /// </summary>
    [YamlMember(Alias = "id")]
    public string? Id { get; set; }
}

/// <summary>Reference to a skill from a package.</summary>
public class SkillReference
{
    /// <summary>Package name.</summary>
    [YamlMember(Alias = "package")]
    public string? Package { get; set; }

    /// <summary>Skill name within the package.</summary>
    [YamlMember(Alias = "skill")]
    public string? Skill { get; set; }
}

/// <summary>
/// A unit member reference. Identifies a peer artefact in the same manifest
/// (a sibling agent or sub-unit) using one of three forms (#1629 PR7,
/// ADR-0043 §5g):
/// <list type="bullet">
///   <item><description>
///     <b>Local symbol</b> — the value of <see cref="Agent"/> or
///     <see cref="Unit"/> is a scalar naming a local symbol scoped to the
///     manifest. The install-time activator maps the symbol to the
///     freshly-minted Guid of the corresponding artefact. Path-style
///     references (<c>scheme://path</c>) are rejected with an actionable
///     error.
///   </description></item>
///   <item><description>
///     <b>Cross-package Guid</b> — a 32-char no-dash hex string (or any form
///     <c>Guid.TryParse</c> accepts) addresses an entity created by a
///     different package. Display-name lookup across packages is gone — names
///     aren't unique, so resolving by name would silently bind to the wrong
///     target.
///   </description></item>
///   <item><description>
///     <b>Inline definition (ADR-0043 §5g)</b> — the value of
///     <see cref="Agent"/> or <see cref="Unit"/> is a YAML mapping carrying a
///     fresh artefact body. When the body declares <c>from:</c> the install
///     pipeline stamps a fresh concrete child by cloning the named template
///     (§5d merge rules) and the inline body's overrides — primarily
///     <c>name:</c> and <c>displayName:</c> — flow through to the persisted
///     child. The inline body's <c>name:</c> serves as the local symbol the
///     unit references; identity is a fresh Guid minted at install time.
///   </description></item>
/// </list>
/// </summary>
public class MemberManifest
{
    /// <summary>
    /// Agent reference — bare scalar (local symbol or cross-package Guid)
    /// or inline body (ADR-0043 §5g) carrying a fresh agent definition.
    /// </summary>
    [YamlMember(Alias = "agent")]
    public InlineArtefactDefinition? Agent { get; set; }

    /// <summary>
    /// Nested-unit reference — bare scalar (local symbol or cross-package
    /// Guid) or inline body (ADR-0043 §5g) carrying a fresh sub-unit
    /// definition.
    /// </summary>
    [YamlMember(Alias = "unit")]
    public InlineArtefactDefinition? Unit { get; set; }

    /// <summary>
    /// Human-participant declaration (ADR-0046 §1). Inline body only —
    /// humans own no sub-artefacts so the folder form is rejected (ADR-0046
    /// §6). The body may also carry <c>from:</c> to stamp from a
    /// <c>HumanTemplate</c> (ADR-0046 §4). Bare-scalar form is reserved for
    /// future cross-package Guid addressing but is not authored in v0.1.
    /// </summary>
    [YamlMember(Alias = "human")]
    public InlineArtefactDefinition? Human { get; set; }

    /// <summary>
    /// Returns the agent reference as a bare string when the member uses
    /// the scalar form, or the inline body's <c>name:</c> when the member
    /// uses the inline form. <c>null</c> when the <c>agent:</c> slot is
    /// absent.
    /// </summary>
    [YamlIgnore]
    public string? AgentName => ExtractName(Agent);

    /// <summary>
    /// Returns the unit reference as a bare string when the member uses the
    /// scalar form, or the inline body's <c>name:</c> when the member uses
    /// the inline form. <c>null</c> when the <c>unit:</c> slot is absent.
    /// </summary>
    [YamlIgnore]
    public string? UnitName => ExtractName(Unit);

    /// <summary>
    /// Returns the human reference as a bare string when the member uses
    /// the scalar form, or the inline body's <c>name:</c> when the member
    /// uses the inline form. <c>null</c> when the <c>human:</c> slot is
    /// absent.
    /// </summary>
    [YamlIgnore]
    public string? HumanName => ExtractName(Human);

    private static string? ExtractName(InlineArtefactDefinition? def)
    {
        if (def is null) return null;
        if (def.Reference is not null) return def.Reference;
        return def.InlineName;
    }
}

/// <summary>
/// Unit-level execution defaults (#601 / #603 / #409 — "B-wide" shape).
/// </summary>
/// <remarks>
/// ADR-0038: <c>execution.provider</c> is removed — the provider is
/// intrinsic to <c>ai.model.provider</c>. <c>execution.tool</c> stays
/// out (dropped in #1732).
/// </remarks>
public class ExecutionManifest
{
    /// <summary>Container image reference.</summary>
    [YamlMember(Alias = "image")]
    public string? Image { get; set; }

    /// <summary>Container runtime identifier (<c>docker</c> or <c>podman</c>).</summary>
    [YamlMember(Alias = "runtime")]
    public string? Runtime { get; set; }

    /// <summary>Default model identifier.</summary>
    [YamlMember(Alias = "model")]
    public string? Model { get; set; }

    /// <summary>
    /// Hosting mode for the unit and its member agents. One of
    /// <c>persistent</c> (default), <c>ephemeral</c>, or <c>pooled</c>
    /// (case-insensitive). Absence means <c>persistent</c>. Member agents
    /// inherit the unit's value when neither the agent nor its template
    /// declares one. Precedence: agent &gt; template &gt; unit &gt; default
    /// (<c>persistent</c>). The manifest parser validates the literal at
    /// parse time (issue #2436); unknown literals (<c>permanent</c>, etc.)
    /// are rejected with a structured <see cref="ManifestParseException"/>.
    /// </summary>
    [YamlMember(Alias = "hosting")]
    public string? Hosting { get; set; }

    /// <summary>True when every field is null / whitespace.</summary>
    [YamlIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Image)
        && string.IsNullOrWhiteSpace(Runtime)
        && string.IsNullOrWhiteSpace(Model)
        && string.IsNullOrWhiteSpace(Hosting);
}

/// <summary>
/// Boundary configuration for a unit (#494).
/// </summary>
public class BoundaryManifest
{
    /// <summary>Opacity rules.</summary>
    [YamlMember(Alias = "opacities")]
    public List<BoundaryOpacityManifestEntry>? Opacities { get; set; }

    /// <summary>Projection rules.</summary>
    [YamlMember(Alias = "projections")]
    public List<BoundaryProjectionManifestEntry>? Projections { get; set; }

    /// <summary>Synthesis rules.</summary>
    [YamlMember(Alias = "syntheses")]
    public List<BoundarySynthesisManifestEntry>? Syntheses { get; set; }

    /// <summary>True when every slot is absent or empty.</summary>
    [YamlIgnore]
    public bool IsEmpty =>
        (Opacities is null || Opacities.Count == 0)
        && (Projections is null || Projections.Count == 0)
        && (Syntheses is null || Syntheses.Count == 0);
}

/// <summary>One opacity rule.</summary>
public class BoundaryOpacityManifestEntry
{
    /// <summary>Domain pattern.</summary>
    [YamlMember(Alias = "domain_pattern")]
    public string? DomainPattern { get; set; }

    /// <summary>Origin pattern.</summary>
    [YamlMember(Alias = "origin_pattern")]
    public string? OriginPattern { get; set; }
}

/// <summary>One projection rule.</summary>
public class BoundaryProjectionManifestEntry
{
    /// <summary>Domain pattern.</summary>
    [YamlMember(Alias = "domain_pattern")]
    public string? DomainPattern { get; set; }

    /// <summary>Origin pattern.</summary>
    [YamlMember(Alias = "origin_pattern")]
    public string? OriginPattern { get; set; }

    /// <summary>Optional rename target.</summary>
    [YamlMember(Alias = "rename_to")]
    public string? RenameTo { get; set; }

    /// <summary>Optional retag value.</summary>
    [YamlMember(Alias = "retag")]
    public string? Retag { get; set; }

    /// <summary>Optional level override.</summary>
    [YamlMember(Alias = "override_level")]
    public string? OverrideLevel { get; set; }
}

/// <summary>One synthesis rule.</summary>
public class BoundarySynthesisManifestEntry
{
    /// <summary>Synthesised domain name.</summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>Domain pattern.</summary>
    [YamlMember(Alias = "domain_pattern")]
    public string? DomainPattern { get; set; }

    /// <summary>Origin pattern.</summary>
    [YamlMember(Alias = "origin_pattern")]
    public string? OriginPattern { get; set; }

    /// <summary>Optional description.</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>Optional explicit level.</summary>
    [YamlMember(Alias = "level")]
    public string? Level { get; set; }
}

/// <summary>
/// Human-participant declaration (ADR-0046 §1, §3). One entry under a unit's
/// <c>members:</c> list with the <c>human:</c> key prefix. Carries the
/// multi-valued team roles, expertise, and notification subscriptions for the
/// participant. Platform ACLs (<c>PermissionLevel</c>) are deliberately NOT a
/// manifest concern — package authors have no authority to grant tenant
/// permissions; the install resolver mints a fresh <see cref="HumanEntity"/>
/// row per declaration (OSS default) or binds to a tenant member (hosted).
/// </summary>
/// <remarks>
/// Per ADR-0046 §3, <c>roles</c> and <c>expertise</c> are case-insensitive sets
/// (duplicates collapse at parse time); empty list and absent field are
/// equivalent. <c>notifications</c> stays human-only — agents have no
/// notification surface, so the field does not appear on agent / unit member
/// bodies.
/// </remarks>
public class HumanManifest
{
    /// <summary>
    /// Optional human-friendly display name. When set, overrides the
    /// install policy's derived default (e.g. OSS "Operator · &lt;roles[0]&gt;")
    /// on the persisted <see cref="HumanEntity.DisplayName"/>.
    /// </summary>
    [YamlMember(Alias = "displayName")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Optional single-line description carried verbatim onto the
    /// persisted <see cref="HumanEntity.Description"/> column. Lets the
    /// portal's Human × Config tab show what the package author intended
    /// this team slot to do.
    /// </summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>
    /// Optional template chain reference (ADR-0046 §4). Bare name resolves
    /// within the package's <c>templates/</c> tree (the
    /// <c>HumanTemplate</c> with matching <c>name:</c>); qualified name
    /// <c>&lt;pkg&gt;/&lt;name&gt;@&lt;version&gt;</c> resolves cross-package per
    /// ADR-0037 §5. When set, the install pipeline clones the template's
    /// fields and overlays this entry's values per ADR-0046 §5 (full
    /// replacement on multi-valued lists).
    /// </summary>
    [YamlMember(Alias = "from")]
    public string? From { get; set; }

    /// <summary>
    /// Free-form team roles the human plays on the unit (e.g.
    /// <c>[owner]</c>, <c>[reviewer, security_lead]</c>). ADR-0046 §3 makes
    /// this multi-valued and case-insensitive within an entry; duplicates
    /// collapse at parse time. Empty list and absent field are equivalent.
    /// </summary>
    [YamlMember(Alias = "roles")]
    public List<string>? Roles { get; set; }

    /// <summary>
    /// Optional list of free-form expertise tags the human brings to the
    /// team (e.g. <c>[security, infra]</c>). Persisted verbatim on the
    /// membership row.
    /// </summary>
    [YamlMember(Alias = "expertise")]
    public List<string>? Expertise { get; set; }

    /// <summary>
    /// Optional list of free-form notification event tags (e.g.
    /// <c>[escalation, completion]</c>). The notification vocabulary +
    /// delivery surface is a separate design pass; the field is captured
    /// at install so that pass has data to design against.
    /// </summary>
    [YamlMember(Alias = "notifications")]
    public List<string>? Notifications { get; set; }
}
