// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;

/// <summary>
/// One entry in an artefact's <c>requires:</c> block (ADR-0037 decision 3).
/// Each entry is a YAML mapping whose first key is the requirement type
/// (<c>connector:</c>, future: <c>secret:</c>, <c>capability:</c>) and
/// whose value is the type-specific binding identifier. The entry may
/// carry additional connector-type-specific sibling keys that pre-seed
/// the binding form at install time (issue #2780); the only one defined
/// today is <c>labels:</c> on <c>connector:</c> entries.
/// </summary>
/// <remarks>
/// <para>
/// The discriminator-as-key shape is intentionally idiomatic with the
/// <c>members:</c> grammar — same parser pattern, same operator mental
/// model. For v0.1 the only supported requirement type is
/// <see cref="RequirementType.Connector"/>; the type system is open for
/// future kinds and a v0.2 follow-up (#1728) will generalise the value
/// shape to versioned <c>&lt;pkg&gt;/&lt;name&gt;@&lt;version&gt;</c>
/// addressing per ADR-0037 decision 5.
/// </para>
/// <para>
/// The package's effective requirement set at install time is the union
/// of every contained artefact's <see cref="UnitManifest.Requires"/> /
/// <see cref="AgentManifest.Requires"/> / etc., deduplicated by
/// <c>(Type, Identifier)</c>. Per-entry defaults (e.g. <see cref="Labels"/>)
/// are folded across artefacts that share the same <c>(Type, Identifier)</c>;
/// conflicting defaults raise a structured parse error so the package
/// author resolves the ambiguity before the operator sees the install
/// wizard.
/// </para>
/// </remarks>
public sealed class RequirementEntry
{
    /// <summary>The requirement type. Today: <see cref="RequirementType.Connector"/>.</summary>
    public RequirementType Type { get; init; }

    /// <summary>
    /// Type-specific binding identifier. For
    /// <see cref="RequirementType.Connector"/> this is the connector slug
    /// (matches <c>Cvoya.Spring.Connectors.IConnectorType.Slug</c>) — e.g.
    /// <c>github</c>. The v0.2 follow-up #1728 extends this to versioned
    /// <c>&lt;pkg&gt;/&lt;name&gt;@&lt;version&gt;</c> addressing.
    /// </summary>
    public string Identifier { get; init; } = string.Empty;

    /// <summary>
    /// Optional label-filter defaults the package author wants to pre-seed
    /// on the binding form at install time (issue #2780). Only meaningful
    /// on connector entries whose slug exposes a label-filter surface
    /// today the GitHub connector). Null when the entry declares no
    /// <c>labels:</c> sibling. The install wizard pre-populates its
    /// include/exclude textareas with the resolved values; the operator
    /// can edit or remove them before submitting, so the final binding
    /// reflects the operator's submission, not these defaults.
    /// </summary>
    public RequirementLabelsBlock? Labels { get; init; }
}

/// <summary>
/// Optional <c>labels:</c> sibling on a <see cref="RequirementEntry"/>
/// (issue #2780). Carries inbound-filter label patterns the package
/// author wants the install wizard to pre-populate on the binding form.
/// </summary>
/// <remarks>
/// <para>
/// Patterns use the same wildcard grammar the GitHub connector's
/// <c>GitHubEventFilter</c> already evaluates (#2563): exact match,
/// <c>*</c>, and <c>prefix:*</c>. The manifest layer does not validate
/// the patterns; that's the consumer connector's job.
/// </para>
/// <para>
/// Two artefacts in the same package may declare the same
/// <c>(Type, Identifier)</c> requirement; their <see cref="Include"/> /
/// <see cref="Exclude"/> lists must match exactly. Differing lists raise
/// a structured <see cref="PackageParseException"/> at validate time so
/// the package author resolves the ambiguity rather than the install
/// pipeline picking arbitrarily.
/// </para>
/// </remarks>
public sealed class RequirementLabelsBlock
{
    /// <summary>
    /// Include patterns. When non-empty, the install wizard pre-populates
    /// the binding's include-labels textarea; the persisted binding's
    /// <c>IncludeLabels</c> field accepts only events whose subject
    /// carries at least one matching label.
    /// </summary>
    public IReadOnlyList<string> Include { get; init; } = System.Array.Empty<string>();

    /// <summary>
    /// Exclude patterns. When non-empty, the install wizard pre-populates
    /// the binding's exclude-labels textarea; the persisted binding's
    /// <c>ExcludeLabels</c> field drops events whose subject carries any
    /// matching label (evaluated before the include check).
    /// </summary>
    public IReadOnlyList<string> Exclude { get; init; } = System.Array.Empty<string>();

    /// <summary>
    /// True when both <see cref="Include"/> and <see cref="Exclude"/> are
    /// empty. Used by the union-fold to skip artefacts that declared the
    /// requirement without defaults and by the wire-shape projection to
    /// omit empty payloads.
    /// </summary>
    public bool IsEmpty => Include.Count == 0 && Exclude.Count == 0;
}

/// <summary>
/// Discriminator for a <see cref="RequirementEntry"/>. The YAML key on the
/// requirement entry maps to one of these values.
/// </summary>
public enum RequirementType
{
    /// <summary>A connector dependency (today's only case).</summary>
    Connector,
}
