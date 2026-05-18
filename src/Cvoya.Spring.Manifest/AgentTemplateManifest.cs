// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;

using YamlDotNet.Serialization;

/// <summary>
/// Typed view of an <c>./templates/&lt;name&gt;/package.yaml</c> document
/// whose <c>kind:</c> discriminator is <c>AgentTemplate</c> (ADR-0043 §5).
/// </summary>
/// <remarks>
/// <para>
/// An <c>AgentTemplate</c> accepts every field that an <see cref="AgentManifest"/>
/// accepts. The discriminator tells the resolver "do not activate me; clone
/// me when a concrete artefact declares <c>from:</c>." The <see cref="From"/>
/// field permits template chaining — a template extending another template.
/// </para>
/// <para>
/// The <c>from:</c>-driven clone operator is wired up in chunk 3 of the
/// ADR-0043 implementation. For chunk 1 the field is parsed but inert.
/// </para>
/// </remarks>
public class AgentTemplateManifest
{
    /// <summary>API version string (e.g. <c>spring.voyage/v1</c>). Required.</summary>
    [YamlMember(Alias = "apiVersion")]
    public string? ApiVersion { get; set; }

    /// <summary>
    /// Document kind discriminator. Must be the literal string
    /// <c>AgentTemplate</c> (ADR-0043 §5a).
    /// </summary>
    [YamlMember(Alias = "kind")]
    public string? Kind { get; set; }

    /// <summary>Template name (required).</summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>
    /// Optional human-friendly label inherited by every instance cloned
    /// from this template (ADR-0043 §5d scalar-merge rule — the instance's
    /// own <c>displayName:</c> still wins). When null, instances fall back
    /// to their own <see cref="Name"/>.
    /// </summary>
    [YamlMember(Alias = "displayName")]
    public string? DisplayName { get; set; }

    /// <summary>Human-readable single-line summary of the template (required).</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>
    /// Optional relative path to a markdown file with long-form prose for
    /// UIs (ADR-0037 decision 2).
    /// </summary>
    [YamlMember(Alias = "readme")]
    public string? Readme { get; set; }

    /// <summary>Optional agent identifier slug used in member references.</summary>
    [YamlMember(Alias = "id")]
    public string? Id { get; set; }

    /// <summary>
    /// Optional template chain reference (ADR-0043 §5e). Bare name resolves
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
    /// ADR-0045 §5 stamping-override slot. When an agent member entry
    /// references this template via <c>- agent: { from: &lt;name&gt;, roles: […] }</c>,
    /// the entry's roles list fully replaces this list on the stamped
    /// instance; otherwise this list flows through unchanged. Free-form
    /// strings; orthogonal to the agent's own <see cref="Role"/> label
    /// (which is a single-valued identity field, not membership-side).
    /// </summary>
    [YamlMember(Alias = "roles")]
    public List<string>? Roles { get; set; }

    /// <summary>
    /// Optional <c>requires:</c> block declaring this template's own
    /// requirements (ADR-0037 decision 3). Each entry is a single-key
    /// mapping (<c>connector: github</c>, etc.).
    /// </summary>
    [YamlMember(Alias = "requires")]
    public List<RequirementEntry>? Requires { get; set; }

    /// <summary>
    /// Optional template-level <c>execution:</c> block. Carries the
    /// container image and hosting mode at the same level as
    /// <c>execution.image</c> on the unit manifest (issue #2436). When a
    /// concrete agent stamped from this template declares its own
    /// <c>execution.hosting</c>, the agent's value wins; otherwise the
    /// agent inherits this template's value, falling back to the unit's
    /// declaration and ultimately to <c>persistent</c>.
    /// </summary>
    [YamlMember(Alias = "execution")]
    public AgentExecutionManifest? Execution { get; set; }
}
