// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Commands;

using System.Collections.Generic;

using YamlDotNet.Serialization;

/// <summary>
/// Root YAML document shape for a unit manifest.
/// Only the <c>unit</c> key is recognised today.
/// </summary>
public class ManifestDocument
{
    /// <summary>The unit definition.</summary>
    [YamlMember(Alias = "unit")]
    public UnitManifest? Unit { get; set; }
}

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