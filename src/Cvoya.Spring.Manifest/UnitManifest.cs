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

    /// <summary>Model identifier (e.g. <c>claude-sonnet-4-20250514</c>).</summary>
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

/// <summary>Execution/runtime description.</summary>
public class ExecutionManifest
{
    /// <summary>Container image reference.</summary>
    [YamlMember(Alias = "image")]
    public string? Image { get; set; }

    /// <summary>Runtime identifier (e.g. <c>docker</c>).</summary>
    [YamlMember(Alias = "runtime")]
    public string? Runtime { get; set; }
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