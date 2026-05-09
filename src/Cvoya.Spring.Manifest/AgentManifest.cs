// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;

using YamlDotNet.Serialization;

/// <summary>
/// Typed view of an <c>./agents/&lt;name&gt;.yaml</c> document under
/// ADR-0037. Each agent YAML is a kind-discriminated top-level document
/// with its own <c>apiVersion</c>, <c>kind</c>, <c>name</c>,
/// <c>description</c>, optional <c>readme</c>, and the agent body fields.
/// The wrapping <c>agent:</c> key from the pre-ADR-0037 grammar is gone.
/// </summary>
/// <remarks>
/// <para>
/// Body fields beyond the headers are intentionally lightweight on the
/// typed side — the activation pipeline consumes the raw post-substitution
/// YAML stored on <see cref="ResolvedArtefact.Content"/> and re-projects it
/// onto its own typed shape. The parser uses <see cref="AgentManifest"/>
/// only to validate the headers and pick out cross-package references for
/// <see cref="CrossPackageCycleDetector"/>.
/// </para>
/// </remarks>
public class AgentManifest
{
    /// <summary>API version string (e.g. <c>spring.voyage/v1</c>). Required.</summary>
    [YamlMember(Alias = "apiVersion")]
    public string? ApiVersion { get; set; }

    /// <summary>
    /// Document kind discriminator. Must be the literal string <c>Agent</c>
    /// (ADR-0037 decision 1).
    /// </summary>
    [YamlMember(Alias = "kind")]
    public string? Kind { get; set; }

    /// <summary>Agent name (required). Hoisted from the legacy nested <c>agent.name</c>.</summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>Human-readable single-line summary of the agent (required).</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>
    /// Optional relative path to a markdown file with long-form prose for
    /// UIs (ADR-0037 decision 2). When omitted, the catalog scanner looks
    /// for a sibling <c>&lt;name&gt;.md</c> and uses it implicitly.
    /// </summary>
    [YamlMember(Alias = "readme")]
    public string? Readme { get; set; }

    /// <summary>Optional agent identifier slug used in member references.</summary>
    [YamlMember(Alias = "id")]
    public string? Id { get; set; }

    /// <summary>Optional agent role label.</summary>
    [YamlMember(Alias = "role")]
    public string? Role { get; set; }

    /// <summary>Optional capability list.</summary>
    [YamlMember(Alias = "capabilities")]
    public List<string>? Capabilities { get; set; }

    /// <summary>Optional AI configuration block.</summary>
    [YamlMember(Alias = "ai")]
    public AiManifest? Ai { get; set; }

    /// <summary>Optional inline instructions / system prompt.</summary>
    [YamlMember(Alias = "instructions")]
    public string? Instructions { get; set; }

    /// <summary>Optional seed expertise entries.</summary>
    [YamlMember(Alias = "expertise")]
    public List<ExpertiseManifestEntry>? Expertise { get; set; }

    /// <summary>
    /// Optional <c>requires:</c> block declaring this agent's own
    /// requirements (ADR-0037 decision 3). Each entry is a single-key
    /// mapping (<c>connector: github</c>, etc.). The package's effective
    /// requirement set is the union of every contained artefact's
    /// <see cref="Requires"/>.
    /// </summary>
    [YamlMember(Alias = "requires")]
    public List<RequirementEntry>? Requires { get; set; }

    /// <summary>
    /// Captured legacy wrapping <c>agent:</c> map. Present only so the
    /// parser can surface an actionable <c>LegacyArtefactWrapper</c> error
    /// per ADR-0037 decision 6 when an old-shape file still wraps the body.
    /// </summary>
    [YamlMember(Alias = "agent")]
    public object? LegacyAgentWrapper { get; set; }
}
