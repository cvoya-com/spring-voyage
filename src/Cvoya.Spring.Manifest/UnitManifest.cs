// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;

using YamlDotNet.Serialization;

/// <summary>
/// Typed view of the <c>unit</c> section in a manifest YAML.
/// Only <see cref="Name"/> is required; all other sections are tolerated when missing.
/// </summary>
public class UnitManifest
{
    /// <summary>The unit's unique name / address path.</summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>Human-readable description of the unit's purpose.</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>Organisational structure hint (e.g. <c>hierarchical</c>).</summary>
    [YamlMember(Alias = "structure")]
    public string? Structure { get; set; }

    /// <summary>
    /// Optional AI/orchestration configuration. Parsed but not yet applied
    /// by the platform API.
    /// </summary>
    [YamlMember(Alias = "ai")]
    public AiManifest? Ai { get; set; }

    /// <summary>
    /// Optional orchestration configuration for the unit. Today this only
    /// carries the <c>strategy</c> key — see #491 — so the unit actor can
    /// resolve the right <c>IOrchestrationStrategy</c> implementation at
    /// dispatch time instead of always binding to the unkeyed default.
    /// Parsed and auto-applied; a <c>null</c> section leaves the unit on the
    /// platform default (inferred to <c>label-routed</c> when the unit also
    /// carries a <c>UnitPolicy.LabelRouting</c> slot, otherwise the
    /// <c>ai</c> default — see
    /// <c>docs/architecture/units.md § Manifest-driven strategy selection</c>).
    /// </summary>
    [YamlMember(Alias = "orchestration")]
    public OrchestrationManifest? Orchestration { get; set; }

    /// <summary>Members of the unit (agents or other units).</summary>
    [YamlMember(Alias = "members")]
    public List<MemberManifest>? Members { get; set; }

    /// <summary>Execution runtime description. Parsed but not yet applied.</summary>
    [YamlMember(Alias = "execution")]
    public ExecutionManifest? Execution { get; set; }

    /// <summary>Connector configurations. Parsed but not yet applied.</summary>
    [YamlMember(Alias = "connectors")]
    public List<ConnectorManifest>? Connectors { get; set; }

    /// <summary>Unit-level policies. Parsed but not yet applied.</summary>
    [YamlMember(Alias = "policies")]
    public Dictionary<string, object>? Policies { get; set; }

    /// <summary>Humans associated with the unit. Parsed but not yet applied.</summary>
    [YamlMember(Alias = "humans")]
    public List<HumanManifest>? Humans { get; set; }

    /// <summary>
    /// Optional seed own-expertise entries for the unit (#488). Each entry
    /// declares a domain the unit advertises in its own right, independent of
    /// its members. Auto-applied to actor state on first activation; runtime
    /// edits through the HTTP / CLI surface remain authoritative thereafter
    /// (see <c>docs/architecture/units.md § Seeding from YAML</c>).
    /// </summary>
    [YamlMember(Alias = "expertise")]
    public List<ExpertiseManifestEntry>? Expertise { get; set; }

    /// <summary>
    /// Optional boundary configuration for the unit (#494 / PR-PLAT-BOUND-2b).
    /// Mirrors the CLI <c>-f</c> YAML shape consumed by
    /// <c>spring unit boundary set</c> and the HTTP
    /// <c>PUT /api/v1/units/{id}/boundary</c> body, so a <c>boundary:</c>
    /// block in a <c>spring apply</c> manifest is wire-equivalent to a
    /// subsequent API call. An absent or empty block leaves the unit with no
    /// boundary rules — the default "transparent" view. See
    /// <c>docs/architecture/units.md § Unit Boundary</c>.
    /// </summary>
    [YamlMember(Alias = "boundary")]
    public BoundaryManifest? Boundary { get; set; }
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
/// Orchestration configuration for a unit (#491). Today only carries the
/// <c>strategy</c> key that names the <see cref="Cvoya.Spring.Core.Orchestration.IOrchestrationStrategy"/>
/// DI registration the unit should resolve at dispatch time. Shipped as its
/// own class rather than a bare <c>string</c> so follow-up work can
/// layer per-strategy options (e.g. workflow image digest, label-routed
/// default timeout) on top without reshaping the manifest grammar.
/// </summary>
public class OrchestrationManifest
{
    /// <summary>
    /// The DI key naming the <see cref="Cvoya.Spring.Core.Orchestration.IOrchestrationStrategy"/>
    /// implementation this unit should use. Expected values today:
    /// <c>ai</c> (default), <c>workflow</c>, <c>label-routed</c>. A host that
    /// registers additional strategies via
    /// <c>AddKeyedScoped&lt;IOrchestrationStrategy, ...&gt;</c> can surface
    /// their keys here without touching the manifest class — the selector
    /// resolves by string key. Unknown keys are rejected at dispatch time
    /// and the unit falls back to the platform default (the
    /// <see cref="Cvoya.Spring.Core.Orchestration.IOrchestrationStrategy"/>
    /// registered unkeyed, which is <c>ai</c> in the OSS stack).
    /// </summary>
    [YamlMember(Alias = "strategy")]
    public string? Strategy { get; set; }
}

/// <summary>AI configuration for a unit (parsed; not yet applied).</summary>
public class AiManifest
{
    /// <summary>Provider agent identifier (e.g. <c>claude</c>).</summary>
    [YamlMember(Alias = "agent")]
    public string? Agent { get; set; }

    /// <summary>Model identifier (e.g. <c>claude-sonnet-4-6</c>).</summary>
    [YamlMember(Alias = "model")]
    public string? Model { get; set; }

    /// <summary>System prompt for the orchestrator.</summary>
    [YamlMember(Alias = "prompt")]
    public string? Prompt { get; set; }

    /// <summary>Skills available to the orchestrator.</summary>
    [YamlMember(Alias = "skills")]
    public List<SkillReference>? Skills { get; set; }
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

/// <summary>A unit member reference.</summary>
public class MemberManifest
{
    /// <summary>Agent name when the member is an agent.</summary>
    [YamlMember(Alias = "agent")]
    public string? Agent { get; set; }

    /// <summary>Nested unit name when the member is another unit.</summary>
    [YamlMember(Alias = "unit")]
    public string? Unit { get; set; }
}

/// <summary>
/// Unit-level execution defaults (#601 / #603 / #409 — "B-wide" shape).
/// A unit's execution block declares the fallback container runtime
/// configuration inherited by member agents that don't declare their own.
/// Every field is independent and independently clearable — a unit may
/// declare only <c>runtime: podman</c> and leave <c>image</c>, <c>tool</c>,
/// etc. to each member agent.
/// </summary>
/// <remarks>
/// <para>
/// Resolution chain (see <c>docs/architecture/units.md</c>): agent.X →
/// unit.X → fail-clean at dispatch / save time. Agent wins on conflict;
/// a missing field on the agent pulls from the unit; when neither
/// declares a required field (image for ephemeral hosting, tool) the
/// platform rejects the configuration at save time rather than waiting
/// for dispatch.
/// </para>
/// <para>
/// <see cref="Provider"/> and <see cref="Model"/> are Dapr-Agent-tool
/// specific (#598 gating) — they're only meaningful when
/// <see cref="Tool"/> = <c>dapr-agent</c>. The portal hides them for
/// other tool selections.
/// </para>
/// </remarks>
public class ExecutionManifest
{
    /// <summary>Container image reference (e.g. <c>ghcr.io/...</c>, <c>localhost/spring-voyage-agent:latest</c>).</summary>
    [YamlMember(Alias = "image")]
    public string? Image { get; set; }

    /// <summary>Container runtime identifier (<c>docker</c> or <c>podman</c>).</summary>
    [YamlMember(Alias = "runtime")]
    public string? Runtime { get; set; }

    /// <summary>
    /// Default external agent tool identifier inherited by member agents
    /// that do not declare their own (e.g. <c>claude-code</c>,
    /// <c>codex</c>, <c>gemini</c>, <c>dapr-agent</c>).
    /// </summary>
    [YamlMember(Alias = "tool")]
    public string? Tool { get; set; }

    /// <summary>
    /// Default LLM provider (Dapr-Agent-tool-specific; see class remarks).
    /// Forwarded to the agent runtime by Dapr-Conversation-backed launchers.
    /// </summary>
    [YamlMember(Alias = "provider")]
    public string? Provider { get; set; }

    /// <summary>
    /// Default model identifier (Dapr-Agent-tool-specific; see class remarks).
    /// Forwarded to the provider by Dapr-Conversation-backed launchers.
    /// </summary>
    [YamlMember(Alias = "model")]
    public string? Model { get; set; }

    /// <summary>
    /// True when every field is null / whitespace. Used by the manifest
    /// applier and portal to decide whether a unit execution write is a
    /// "clear" rather than a "set".
    /// </summary>
    [YamlIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Image)
        && string.IsNullOrWhiteSpace(Runtime)
        && string.IsNullOrWhiteSpace(Tool)
        && string.IsNullOrWhiteSpace(Provider)
        && string.IsNullOrWhiteSpace(Model);
}

/// <summary>Connector configuration entry.</summary>
public class ConnectorManifest
{
    /// <summary>Connector type (e.g. <c>github</c>).</summary>
    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    /// <summary>Free-form connector configuration.</summary>
    [YamlMember(Alias = "config")]
    public Dictionary<string, object>? Config { get; set; }
}

/// <summary>
/// Boundary configuration for a unit (#494). Matches the three-list YAML
/// grammar consumed by <c>spring unit boundary set -f</c> and the HTTP
/// <c>PUT /api/v1/units/{id}/boundary</c> body, so the same fragment can be
/// authored once and persisted through either path without loss. Each slot
/// is optional; an all-null or all-empty block is equivalent to "no
/// boundary" (the transparent view). Shipped as its own class rather than a
/// bare dictionary so a schema validator / portal form can bind directly to
/// the typed shape.
/// </summary>
public class BoundaryManifest
{
    /// <summary>
    /// Opacity rules — every matching <c>ExpertiseEntry</c> is stripped from
    /// the outside view. Rules OR together.
    /// </summary>
    [YamlMember(Alias = "opacities")]
    public List<BoundaryOpacityManifestEntry>? Opacities { get; set; }

    /// <summary>
    /// Projection rules — every matching entry is rewritten (new name /
    /// description / level). First matching rule wins.
    /// </summary>
    [YamlMember(Alias = "projections")]
    public List<BoundaryProjectionManifestEntry>? Projections { get; set; }

    /// <summary>
    /// Synthesis rules — every matching set of entries is collapsed into a
    /// single synthesised entry attributed to the unit.
    /// </summary>
    [YamlMember(Alias = "syntheses")]
    public List<BoundarySynthesisManifestEntry>? Syntheses { get; set; }

    /// <summary>
    /// True when every slot is absent or empty. Consumers use this to decide
    /// whether a boundary write needs to fire at all — a unit with an empty
    /// manifest block is indistinguishable from one that declared no boundary.
    /// </summary>
    [YamlIgnore]
    public bool IsEmpty =>
        (Opacities is null || Opacities.Count == 0)
        && (Projections is null || Projections.Count == 0)
        && (Syntheses is null || Syntheses.Count == 0);
}

/// <summary>
/// One opacity rule inside a <see cref="BoundaryManifest.Opacities"/> list.
/// A matched entry is removed from the outside view.
/// </summary>
public class BoundaryOpacityManifestEntry
{
    /// <summary>
    /// Case-insensitive exact-match or <c>*</c>-suffix pattern on the
    /// aggregated entry's domain name. <c>null</c> matches any domain.
    /// </summary>
    [YamlMember(Alias = "domain_pattern")]
    public string? DomainPattern { get; set; }

    /// <summary>
    /// Optional <c>scheme://path</c> pattern matched against the entry's
    /// origin. <c>null</c> matches any origin.
    /// </summary>
    [YamlMember(Alias = "origin_pattern")]
    public string? OriginPattern { get; set; }
}

/// <summary>
/// One projection rule inside a <see cref="BoundaryManifest.Projections"/>
/// list. A matched entry is rewritten — rename / retag / relevel — and still
/// emitted to outside callers.
/// </summary>
public class BoundaryProjectionManifestEntry
{
    /// <summary>Same semantics as <see cref="BoundaryOpacityManifestEntry.DomainPattern"/>.</summary>
    [YamlMember(Alias = "domain_pattern")]
    public string? DomainPattern { get; set; }

    /// <summary>Same semantics as <see cref="BoundaryOpacityManifestEntry.OriginPattern"/>.</summary>
    [YamlMember(Alias = "origin_pattern")]
    public string? OriginPattern { get; set; }

    /// <summary>Optional replacement for the domain name. <c>null</c> leaves it unchanged.</summary>
    [YamlMember(Alias = "rename_to")]
    public string? RenameTo { get; set; }

    /// <summary>Optional replacement for the domain description. <c>null</c> leaves it unchanged.</summary>
    [YamlMember(Alias = "retag")]
    public string? Retag { get; set; }

    /// <summary>
    /// Optional replacement for the domain level — one of
    /// <c>beginner | intermediate | advanced | expert</c> (case-insensitive).
    /// Unrecognised values are persisted as-is but resolved to <c>null</c>
    /// at read time, matching the HTTP DTO's tolerance.
    /// </summary>
    [YamlMember(Alias = "override_level")]
    public string? OverrideLevel { get; set; }
}

/// <summary>
/// One synthesis rule inside a <see cref="BoundaryManifest.Syntheses"/>
/// list. Matching entries are removed and replaced with a single synthesised
/// entry attributed to the unit.
/// </summary>
public class BoundarySynthesisManifestEntry
{
    /// <summary>
    /// Name of the synthesised domain. Required — a synthesis rule with no
    /// name is silently skipped by the persistence layer so a malformed
    /// entry never fabricates an empty team capability.
    /// </summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>Same semantics as <see cref="BoundaryOpacityManifestEntry.DomainPattern"/>.</summary>
    [YamlMember(Alias = "domain_pattern")]
    public string? DomainPattern { get; set; }

    /// <summary>Same semantics as <see cref="BoundaryOpacityManifestEntry.OriginPattern"/>.</summary>
    [YamlMember(Alias = "origin_pattern")]
    public string? OriginPattern { get; set; }

    /// <summary>Optional description attached to the synthesised domain.</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>
    /// Optional explicit level for the synthesised capability. When
    /// <c>null</c> the server uses the strongest level observed across
    /// matched entries. Same tolerance rule as
    /// <see cref="BoundaryProjectionManifestEntry.OverrideLevel"/>.
    /// </summary>
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