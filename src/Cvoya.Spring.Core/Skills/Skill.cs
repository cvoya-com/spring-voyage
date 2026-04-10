// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

/// <summary>
/// Represents a skill that an agent can use, consisting of a name, description,
/// and a set of tool definitions.
/// </summary>
/// <param name="Name">The name of the skill.</param>
/// <param name="Description">A description of what the skill does.</param>
/// <param name="Tools">The tool definitions that make up this skill.</param>
public record Skill(string Name, string Description, IReadOnlyList<ToolDefinition> Tools);
