// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;

using YamlDotNet.Serialization;

/// <summary>
/// Typed view of a <c>./templates/&lt;name&gt;/package.yaml</c> document whose
/// <c>kind:</c> discriminator is <c>HumanTemplate</c> (ADR-0046 §4). Mirrors
/// the shape of <see cref="AgentTemplateManifest"/> and
/// <see cref="UnitTemplateManifest"/>: the resolver routes by the
/// discriminator and stamps fresh concrete bodies when a member entry
/// declares <c>- human: { from: &lt;template-name&gt; }</c>.
/// </summary>
/// <remarks>
/// <para>
/// Humans own no sub-artefacts, so a <c>HumanTemplate</c> folder carries no
/// <c>units/</c> / <c>agents/</c> / <c>skills/</c> / <c>templates/</c>
/// children. The stamping operator (ADR-0046 §5) clones the template's
/// scalar fields and applies full-replacement semantics on the
/// <see cref="Roles"/>, <see cref="Expertise"/>, and <see cref="Notifications"/>
/// lists — the member entry's values fully replace the template's values
/// when present, otherwise the template's values flow through unchanged.
/// </para>
/// </remarks>
public class HumanTemplateManifest
{
    /// <summary>API version string (e.g. <c>spring.voyage/v1</c>). Required.</summary>
    [YamlMember(Alias = "apiVersion")]
    public string? ApiVersion { get; set; }

    /// <summary>
    /// Document kind discriminator. Must be the literal string
    /// <c>HumanTemplate</c> (ADR-0046 §4).
    /// </summary>
    [YamlMember(Alias = "kind")]
    public string? Kind { get; set; }

    /// <summary>Template name (required). Matches the containing folder name.</summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>
    /// Optional human-friendly label inherited by every human stamped from
    /// this template. The stamping entry's <c>displayName:</c> wins per
    /// ADR-0046 §5 (scalar override).
    /// </summary>
    [YamlMember(Alias = "displayName")]
    public string? DisplayName { get; set; }

    /// <summary>
    /// Optional single-line description inherited by stamped humans. The
    /// stamping entry's <c>description:</c> wins per ADR-0046 §5.
    /// </summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>
    /// Optional relative path to a markdown file with long-form prose for
    /// UIs (ADR-0037 decision 2).
    /// </summary>
    [YamlMember(Alias = "readme")]
    public string? Readme { get; set; }

    /// <summary>
    /// Optional template chain reference (ADR-0046 §4, ADR-0043 §5e). Bare
    /// name resolves within the package; qualified name
    /// <c>&lt;pkg&gt;/&lt;name&gt;@&lt;version&gt;</c> resolves cross-package per
    /// ADR-0037 §5. Lets a tenant-specific <c>HumanTemplate</c> extend a
    /// shared OSS-operator definition without re-listing its roles.
    /// </summary>
    [YamlMember(Alias = "from")]
    public string? From { get; set; }

    /// <summary>
    /// Free-form team roles every stamped human inherits (ADR-0046 §3).
    /// Multi-valued; the stamping member entry's <see cref="HumanManifest.Roles"/>
    /// fully replaces this list when present (ADR-0046 §5).
    /// </summary>
    [YamlMember(Alias = "roles")]
    public List<string>? Roles { get; set; }

    /// <summary>
    /// Free-form expertise tags every stamped human inherits (ADR-0046 §3).
    /// Multi-valued; the stamping member entry's expertise list fully
    /// replaces this list when present.
    /// </summary>
    [YamlMember(Alias = "expertise")]
    public List<string>? Expertise { get; set; }

    /// <summary>
    /// Free-form notification event tags every stamped human inherits.
    /// The stamping member entry's notifications list fully replaces this
    /// list when present (ADR-0046 §5).
    /// </summary>
    [YamlMember(Alias = "notifications")]
    public List<string>? Notifications { get; set; }

    /// <summary>
    /// Optional <c>requires:</c> block declaring this template's own
    /// requirements (ADR-0037 decision 3). Each entry is a single-key
    /// mapping (<c>connector: github</c>, etc.).
    /// </summary>
    [YamlMember(Alias = "requires")]
    public List<RequirementEntry>? Requires { get; set; }
}
