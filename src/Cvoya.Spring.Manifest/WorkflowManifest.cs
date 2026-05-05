// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Manifest;

using System.Collections.Generic;

using YamlDotNet.Serialization;

/// <summary>
/// Typed view of a <c>./workflows/&lt;name&gt;/workflow.yaml</c> document
/// under ADR-0037. Each workflow YAML is a kind-discriminated top-level
/// document with its own <c>apiVersion</c>, <c>kind</c>, <c>name</c>,
/// <c>description</c>, optional <c>readme</c>, and optional <c>requires</c>.
/// </summary>
public class WorkflowManifest
{
    /// <summary>API version string (e.g. <c>spring.voyage/v1</c>).</summary>
    [YamlMember(Alias = "apiVersion")]
    public string? ApiVersion { get; set; }

    /// <summary>Document kind. Must be the literal string <c>Workflow</c>.</summary>
    [YamlMember(Alias = "kind")]
    public string? Kind { get; set; }

    /// <summary>Workflow name. Required.</summary>
    [YamlMember(Alias = "name")]
    public string? Name { get; set; }

    /// <summary>Human-readable single-line summary. Required.</summary>
    [YamlMember(Alias = "description")]
    public string? Description { get; set; }

    /// <summary>
    /// Optional relative path to a markdown file with long-form prose for
    /// UIs.
    /// </summary>
    [YamlMember(Alias = "readme")]
    public string? Readme { get; set; }

    /// <summary>
    /// Optional <c>requires:</c> block (ADR-0037 decision 3).
    /// </summary>
    [YamlMember(Alias = "requires")]
    public List<RequirementEntry>? Requires { get; set; }
}
