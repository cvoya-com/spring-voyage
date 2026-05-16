// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;

using YamlDotNet.Serialization;

/// <summary>
/// Typed view of a <c>./templates/&lt;name&gt;/package.yaml</c> document
/// whose <c>kind:</c> discriminator is <c>UnitTemplate</c> (ADR-0043 §5).
/// </summary>
/// <remarks>
/// <para>
/// A <c>UnitTemplate</c> accepts every field that a <see cref="UnitManifest"/>
/// accepts. The discriminator tells the resolver "do not activate me; clone
/// me when a concrete artefact declares <c>from:</c>." The <see cref="From"/>
/// field permits template chaining — a template extending another template.
/// </para>
/// <para>
/// The <c>from:</c>-driven clone operator is wired up in chunk 3 of the
/// ADR-0043 implementation. For chunk 1 the field is parsed but inert.
/// </para>
/// </remarks>
public class UnitTemplateManifest
{
    /// <summary>API version string (e.g. <c>spring.voyage/v1</c>).</summary>
    [YamlMember(Alias = "apiVersion")]
    public string? ApiVersion { get; set; }

    /// <summary>
    /// Document kind. Must be the literal string <c>UnitTemplate</c>
    /// (ADR-0043 §5a).
    /// </summary>
    [YamlMember(Alias = "kind")]
    public string? Kind { get; set; }

    /// <summary>The template's unique name.</summary>
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

    /// <summary>Human-readable single-line description of the template.</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>
    /// Optional relative path to a markdown file with long-form prose for
    /// UIs (ADR-0037 decision 2).
    /// </summary>
    [YamlMember(Alias = "readme")]
    public string? Readme { get; set; }

    /// <summary>
    /// Optional template chain reference (ADR-0043 §5e). Bare name resolves
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
    /// User-provided prompt for the template. Top-level slot per ADR-0043
    /// + #2298; aligns with the hoisted slot on <see cref="UnitManifest"/>.
    /// </summary>
    [YamlMember(Alias = "instructions")]
    public string? Instructions { get; set; }

    /// <summary>Members of the template (agents or sub-units).</summary>
    [YamlMember(Alias = "members")]
    public List<MemberManifest>? Members { get; set; }

    /// <summary>Execution runtime description. Parsed but not yet applied.</summary>
    [YamlMember(Alias = "execution")]
    public ExecutionManifest? Execution { get; set; }

    /// <summary>
    /// Optional <c>requires:</c> block declaring this template's own
    /// requirements (ADR-0037 decision 3). Each entry is a single-key
    /// mapping (<c>connector: github</c>, etc.).
    /// </summary>
    [YamlMember(Alias = "requires")]
    public List<RequirementEntry>? Requires { get; set; }

    /// <summary>Template-level policies. Parsed but not yet applied.</summary>
    [YamlMember(Alias = "policies")]
    public Dictionary<string, object>? Policies { get; set; }

    /// <summary>Humans associated with the template. Parsed but not yet applied.</summary>
    [YamlMember(Alias = "humans")]
    public List<HumanManifest>? Humans { get; set; }

    /// <summary>
    /// Optional seed own-expertise entries for the template (#488).
    /// </summary>
    [YamlMember(Alias = "expertise")]
    public List<ExpertiseManifestEntry>? Expertise { get; set; }

    /// <summary>
    /// Optional boundary configuration for the template (#494).
    /// </summary>
    [YamlMember(Alias = "boundary")]
    public BoundaryManifest? Boundary { get; set; }
}
