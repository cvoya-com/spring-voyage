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

    /// <summary>Humans associated with the unit. Parsed but not yet applied.</summary>
    [YamlMember(Alias = "humans")]
    public List<HumanManifest>? Humans { get; set; }

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

    /// <summary>
    /// Captured legacy wrapping <c>unit:</c> map. Present only so the
    /// parser can surface an actionable <c>LegacyArtefactWrapper</c>
    /// error per ADR-0037 decision 6 when an old-shape file still wraps
    /// the body.
    /// </summary>
    [YamlMember(Alias = "unit")]
    public object? LegacyUnitWrapper { get; set; }

    /// <summary>
    /// Captured legacy <c>structure:</c> field. Present only so the
    /// parser can surface an actionable <c>LegacyStructureField</c>
    /// error per ADR-0037 decision 6. The membership graph already
    /// encodes the structure.
    /// </summary>
    [YamlMember(Alias = "structure")]
    public string? LegacyStructure { get; set; }

    /// <summary>
    /// Captured legacy <c>connectors:</c> block. Present only so the
    /// parser can surface an actionable <c>LegacyUnitConnectorsField</c>
    /// error per ADR-0037 decision 6. Use <see cref="Requires"/>
    /// instead.
    /// </summary>
    [YamlMember(Alias = "connectors")]
    public List<object>? LegacyConnectors { get; set; }
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
/// <remarks>
/// The legacy slots — <c>ai.agent</c> (pre-ADR-0038 runtime selector)
/// and the string form of <c>ai.model</c> — are captured on
/// <see cref="LegacyAgent"/> / <see cref="LegacyModelString"/> so the
/// parser can surface precise migration hints (ADR-0038 § "Migration").
/// </remarks>
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

    /// <summary>Skills available to the orchestrator.</summary>
    [YamlMember(Alias = "skills")]
    public List<SkillReference>? Skills { get; set; }

    /// <summary>
    /// Captured legacy <c>ai.agent</c> field. Present only so the parser
    /// can surface a precise <c>LegacyAiAgentField</c> migration error
    /// per ADR-0038 § "Migration".
    /// </summary>
    [YamlMember(Alias = "agent")]
    public string? LegacyAgent { get; set; }

    /// <summary>
    /// Captured legacy <c>ai.model</c> field when authored as a string
    /// instead of the new <c>{provider, id}</c> object form. Present
    /// only so the parser can surface a precise
    /// <c>LegacyAiModelStringForm</c> migration error per ADR-0038
    /// § "Migration".
    /// </summary>
    /// <remarks>
    /// The parser inspects the raw YAML and populates this field when
    /// <c>ai.model</c> is a scalar — <see cref="Model"/> stays null in
    /// that case and the legacy detection branch fires.
    /// </remarks>
    [YamlIgnore]
    public string? LegacyModelString { get; set; }
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
/// (a sibling agent or sub-unit) using one of two forms (#1629 PR7):
/// <list type="bullet">
///   <item><description>
///     <b>Local symbol</b> — the value of <see cref="Agent"/> or
///     <see cref="Unit"/> names a local symbol scoped to the manifest. The
///     install-time activator maps the symbol to the freshly-minted Guid of
///     the corresponding artefact. Path-style references
///     (<c>scheme://path</c>) are rejected with an actionable error.
///   </description></item>
///   <item><description>
///     <b>Cross-package Guid</b> — a 32-char no-dash hex string (or any form
///     <c>Guid.TryParse</c> accepts) addresses an entity created by a
///     different package. Display-name lookup across packages is gone — names
///     aren't unique, so resolving by name would silently bind to the wrong
///     target.
///   </description></item>
/// </list>
/// </summary>
public class MemberManifest
{
    /// <summary>
    /// Agent reference — either a local symbol (peer agent in the same
    /// manifest) or a 32-char no-dash hex Guid (cross-package).
    /// </summary>
    [YamlMember(Alias = "agent")]
    public string? Agent { get; set; }

    /// <summary>
    /// Nested-unit reference — either a local symbol (peer unit in the same
    /// manifest) or a 32-char no-dash hex Guid (cross-package).
    /// </summary>
    [YamlMember(Alias = "unit")]
    public string? Unit { get; set; }
}

/// <summary>
/// Unit-level execution defaults (#601 / #603 / #409 — "B-wide" shape).
/// </summary>
/// <remarks>
/// ADR-0038: <c>execution.provider</c> is removed — the provider is
/// intrinsic to <c>ai.model.provider</c>. <c>execution.tool</c> stays
/// out (dropped in #1732). The corresponding capture slots
/// (<see cref="LegacyTool"/>, <see cref="LegacyProvider"/>) survive
/// only so the parser can surface precise migration errors when an
/// old-shape file still declares them.
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
    /// Captured legacy <c>tool:</c> field. Present only so the parser can
    /// surface an actionable <c>LegacyExecutionToolField</c> error per
    /// ADR-0037 decision 6 / #1732 when an old-shape file still carries it.
    /// </summary>
    [YamlMember(Alias = "tool")]
    public string? LegacyTool { get; set; }

    /// <summary>
    /// Captured legacy <c>provider:</c> field. Present only so the parser
    /// can surface an actionable <c>LegacyExecutionProviderField</c>
    /// error per ADR-0038 § "Migration" when an old-shape file still
    /// carries it.
    /// </summary>
    [YamlMember(Alias = "provider")]
    public string? LegacyProvider { get; set; }

    /// <summary>True when every field is null / whitespace.</summary>
    [YamlIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Image)
        && string.IsNullOrWhiteSpace(Runtime)
        && string.IsNullOrWhiteSpace(Model);
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

/// <summary>Human participant declaration.</summary>
public class HumanManifest
{
    /// <summary>Human identity key.</summary>
    [YamlMember(Alias = "identity")]
    public string? Identity { get; set; }

    /// <summary>Permission level (e.g. <c>owner</c>).</summary>
    [YamlMember(Alias = "permission")]
    public string? Permission { get; set; }

    /// <summary>Notification subscriptions.</summary>
    [YamlMember(Alias = "notifications")]
    public List<string>? Notifications { get; set; }
}
