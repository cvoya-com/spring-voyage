// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using System.Text.Json;

/// <summary>
/// A single tool requirement declared by a <see cref="SkillBundle"/>. The bundle
/// asks the platform to expose a tool with this name and input schema; it does
/// NOT provide the implementation. The platform is responsible for matching the
/// requirement to a concrete tool surfaced by an <see cref="ISkillRegistry"/>
/// (e.g., a GitHub connector) at unit-creation time.
/// </summary>
/// <param name="Name">The tool name the bundle expects (matched case-insensitively against registered tools).</param>
/// <param name="Description">Human-readable description copied from the bundle's <c>.tools.json</c>.</param>
/// <param name="Schema">The JSON-schema <c>parameters</c> object declared for the tool.</param>
/// <param name="Optional">
/// When <c>true</c>, manifest validation tolerates the tool not being surfaced
/// by any registry. Useful for experimental skills. Defaults to <c>false</c>
/// — a missing required tool fails unit creation with a clear diagnostic.
/// </param>
public record SkillToolRequirement(
    string Name,
    string Description,
    JsonElement Schema,
    bool Optional = false);