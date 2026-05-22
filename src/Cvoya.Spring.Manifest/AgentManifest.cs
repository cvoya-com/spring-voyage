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

    /// <summary>
    /// Optional human-friendly label for the agent. When set, surfaces as
    /// the agent's <c>DisplayName</c> on the persisted directory entry and
    /// in every read API / CLI / portal projection; when null or
    /// whitespace, the persistence layer falls back to <see cref="Name"/>.
    /// An install-time operator override still wins for the package's
    /// top-level activatable (AgentPackage shape).
    /// </summary>
    [YamlMember(Alias = "displayName")]
    public string? DisplayName { get; set; }

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

    /// <summary>
    /// Optional template reference (ADR-0043 §5). Bare name resolves
    /// within the package; qualified name <c>&lt;pkg&gt;/&lt;name&gt;@&lt;version&gt;</c>
    /// resolves cross-package per ADR-0037 §5.
    /// </summary>
    [YamlMember(Alias = "from")]
    public string? From { get; set; }

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
    /// Optional agent-level <c>execution:</c> block. Carries the
    /// container image and hosting mode at the same level as
    /// <c>execution.image</c> on the unit manifest (issue #2436).
    /// </summary>
    [YamlMember(Alias = "execution")]
    public AgentExecutionManifest? Execution { get; set; }

    /// <summary>Optional specialty label surfaced for agent selection; the platform does not route on it.</summary>
    [YamlMember(Alias = "specialty")]
    public string? Specialty { get; set; }

    /// <summary>Whether this agent processes inbound messages. Defaults to true.</summary>
    [YamlMember(Alias = "enabled")]
    public bool? Enabled { get; set; }

    /// <summary>How this agent participates in dispatch (auto or onDemand).</summary>
    [YamlMember(Alias = "executionMode")]
    public string? ExecutionMode { get; set; }
}

/// <summary>
/// Typed view of an agent / agent-template <c>execution:</c> block.
/// Converges with the unit-side <see cref="ExecutionManifest"/> on
/// <c>{image, hosting}</c> per the ADR-0038 amendment (#2634). The
/// runtime and model are authored under the <c>ai:</c> block
/// (<c>ai.runtime</c>, <c>ai.model{provider, id}</c>).
/// </summary>
public class AgentExecutionManifest
{
    /// <summary>Container image reference.</summary>
    [YamlMember(Alias = "image")]
    public string? Image { get; set; }

    /// <summary>
    /// Hosting mode for the agent. One of <c>persistent</c> (default),
    /// <c>ephemeral</c>, or <c>pooled</c> (case-insensitive). Absence means
    /// "inherit from the unit / template, falling back to <c>persistent</c>"
    /// (precedence: agent &gt; template &gt; unit &gt; default). The
    /// manifest parser validates the literal at parse time (issue #2436);
    /// unknown literals (<c>permanent</c>, etc.) are rejected with a
    /// structured <see cref="ManifestParseException"/>.
    /// </summary>
    [YamlMember(Alias = "hosting")]
    public string? Hosting { get; set; }

    /// <summary>True when every field is null / whitespace.</summary>
    [YamlIgnore]
    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(Image)
        && string.IsNullOrWhiteSpace(Hosting);
}
