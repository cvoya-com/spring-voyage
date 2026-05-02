// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;

using YamlDotNet.Serialization;

/// <summary>
/// Discriminates the kind of package declared in a <c>package.yaml</c>.
/// Matches the <c>kind:</c> scalar on the root document.
/// </summary>
public enum PackageKind
{
    /// <summary>The package bundles a unit (a composite of agents and/or sub-units).</summary>
    UnitPackage,

    /// <summary>The package bundles a single agent.</summary>
    AgentPackage,
}

/// <summary>
/// Root document for a <c>package.yaml</c> manifest. Parsed by
/// <see cref="PackageManifestParser"/>. A package declares its kind,
/// metadata, inputs schema, and the root artefact it wraps.
/// </summary>
/// <remarks>
/// The package YAML shape (decision 2 in ADR-0035):
/// <code>
/// apiVersion: spring.voyage/v1
/// kind: UnitPackage            # or AgentPackage
/// metadata:
///   name: my-package
///   description: ...
/// inputs:
///   - name: team_name
///     type: string
///     required: true
/// unit: sv-oss-design          # bare = ./units/sv-oss-design.yaml
///                              # qualified = other-pkg/sv-oss-design
/// </code>
/// </remarks>
public class PackageManifest
{
    /// <summary>API version string (e.g. <c>spring.voyage/v1</c>).</summary>
    [YamlMember(Alias = "apiVersion")]
    public string? ApiVersion { get; set; }

    /// <summary>Package kind. Must be <c>UnitPackage</c> or <c>AgentPackage</c>.</summary>
    [YamlMember(Alias = "kind")]
    public string? Kind { get; set; }

    /// <summary>Package-level metadata (name, description, etc.).</summary>
    [YamlMember(Alias = "metadata")]
    public PackageMetadata? Metadata { get; set; }

    /// <summary>
    /// Declared inputs for the package. Each entry defines a scalar input
    /// that may be referenced as <c>${{ inputs.&lt;name&gt; }}</c> in any
    /// YAML value within the package.
    /// </summary>
    [YamlMember(Alias = "inputs")]
    public List<PackageInputDefinition>? Inputs { get; set; }

    /// <summary>
    /// The root unit reference (used when <see cref="Kind"/> is
    /// <c>UnitPackage</c>). Bare name resolves to
    /// <c>./units/&lt;name&gt;.yaml</c>; qualified name (<c>pkg/name</c>)
    /// resolves via the catalog.
    /// </summary>
    [YamlMember(Alias = "unit")]
    public string? Unit { get; set; }

    /// <summary>
    /// The root agent reference (used when <see cref="Kind"/> is
    /// <c>AgentPackage</c>). Bare name resolves to
    /// <c>./agents/&lt;name&gt;.yaml</c>; qualified name (<c>pkg/name</c>)
    /// resolves via the catalog.
    /// </summary>
    [YamlMember(Alias = "agent")]
    public string? Agent { get; set; }

    /// <summary>
    /// Additional sub-unit references. Each entry resolves the same way as
    /// <see cref="Unit"/>. These represent artefacts bundled alongside the
    /// root unit in a <c>UnitPackage</c>.
    /// </summary>
    [YamlMember(Alias = "subUnits")]
    public List<string>? SubUnits { get; set; }

    /// <summary>
    /// Skill references bundled in the package. Bare name resolves to
    /// <c>./skills/&lt;name&gt;.md</c>.
    /// </summary>
    [YamlMember(Alias = "skills")]
    public List<string>? Skills { get; set; }

    /// <summary>
    /// Workflow references bundled in the package. Bare name resolves to
    /// <c>./workflows/&lt;name&gt;/</c>.
    /// </summary>
    [YamlMember(Alias = "workflows")]
    public List<string>? Workflows { get; set; }
}

/// <summary>
/// Package-level metadata block.
/// </summary>
public class PackageMetadata
{
    /// <summary>Unique package name (must be a valid identifier).</summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>Human-readable description of the package.</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>Optional display name for the package.</summary>
    [YamlMember(Alias = "displayName")]
    public string? DisplayName { get; set; }
}

/// <summary>
/// Declares a single scalar input for a package. Input types are
/// <c>string</c>, <c>int</c>, <c>bool</c>, and <c>secret</c>
/// (per ADR-0035 decision 8).
/// </summary>
public class PackageInputDefinition
{
    /// <summary>Input key name. Used in <c>${{ inputs.&lt;name&gt; }}</c> expressions.</summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>
    /// Scalar type: <c>string</c> (default), <c>int</c>, or <c>bool</c>.
    /// Use <see cref="Secret"/> for secret-typed inputs.
    /// </summary>
    [YamlMember(Alias = "type")]
    public string? Type { get; set; }

    /// <summary>When <c>true</c>, the input must be supplied; an absent value is a parse error.</summary>
    [YamlMember(Alias = "required")]
    public bool Required { get; set; }

    /// <summary>
    /// When <c>true</c>, the input is secret-typed. The value is stored as a
    /// secret reference, not as cleartext. Secret inputs are never round-tripped
    /// in export output as plain values.
    /// </summary>
    [YamlMember(Alias = "secret")]
    public bool Secret { get; set; }

    /// <summary>Human-readable description of the input's purpose.</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>
    /// Optional default value (ignored for <c>required: true</c> inputs when
    /// no value is supplied — a required input with no value is an error even
    /// if a default is declared).
    /// </summary>
    [YamlMember(Alias = "default")]
    public string? Default { get; set; }
}