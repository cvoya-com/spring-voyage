// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

/// <summary>
/// One entry in an artefact's <c>requires:</c> block (ADR-0037 decision 3).
/// Each entry is a single-key YAML mapping whose key is the requirement
/// type (<c>connector:</c>, future: <c>secret:</c>, <c>capability:</c>) and
/// whose value is the type-specific binding identifier.
/// </summary>
/// <remarks>
/// <para>
/// The discriminator-as-key shape matches <see cref="ContentEntry"/> from
/// PR #1719 — same parser pattern, same operator mental model. For v0.1
/// the only supported requirement type is
/// <see cref="RequirementType.Connector"/>; the type system is open for
/// future kinds and a v0.2 follow-up (#1728) will generalise the value
/// shape to versioned <c>&lt;pkg&gt;/&lt;name&gt;@&lt;version&gt;</c>
/// addressing per ADR-0037 decision 5.
/// </para>
/// <para>
/// The package's effective requirement set at install time is the union
/// of every contained artefact's <see cref="UnitManifest.Requires"/> /
/// <see cref="AgentManifest.Requires"/> / etc., deduplicated by
/// <c>(Type, Identifier)</c>.
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