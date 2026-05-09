// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;

using YamlDotNet.Serialization;

/// <summary>
/// Typed view of a skill artefact's frontmatter under ADR-0037. Skill
/// files keep the <c>./skills/&lt;name&gt;.md</c> markdown form
/// established by ADR-0035 §2; the markdown gains an optional YAML
/// frontmatter block carrying the same kind-discriminated headers used by
/// every other artefact YAML — <c>apiVersion</c>, <c>kind: Skill</c>,
/// <c>name</c>, <c>description</c>, optional <c>readme</c>, optional
/// <c>requires</c>.
/// </summary>
/// <remarks>
/// The frontmatter is optional in v0.1 to keep the migration tractable;
/// the parser falls back to the file basename for <see cref="Name"/> and
/// the first paragraph for <see cref="Description"/> when the frontmatter
/// is absent. Skill content (the body of the markdown after the
/// frontmatter) is consumed verbatim by the skill resolver downstream.
/// </remarks>
public class SkillManifest
{
    /// <summary>API version string (e.g. <c>spring.voyage/v1</c>).</summary>
    [YamlMember(Alias = "apiVersion")]
    public string? ApiVersion { get; set; }

    /// <summary>Document kind. Must be the literal string <c>Skill</c>.</summary>
    [YamlMember(Alias = "kind")]
    public string? Kind { get; set; }

    /// <summary>Skill name. Falls back to the file basename when absent.</summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>Human-readable single-line summary of the skill.</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>
    /// Optional relative path to a markdown file with long-form prose for
    /// UIs. Skills already live in markdown; <c>readme:</c> exists for
    /// symmetry with other artefact kinds and for cases where the long
    /// form lives in a sibling file rather than below the frontmatter.
    /// </summary>
    [YamlMember(Alias = "readme")]
    public string? Readme { get; set; }

    /// <summary>
    /// Optional <c>requires:</c> block declaring this skill's own
    /// requirements (ADR-0037 decision 3). Skills that talk to a connector
    /// (e.g. a GitHub triage skill that calls the GitHub API) declare it
    /// here so the package's effective requirement set covers them.
    /// </summary>
    [YamlMember(Alias = "requires")]
    public List<RequirementEntry>? Requires { get; set; }
}
