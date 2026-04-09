/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Core.Skills;

/// <summary>
/// Represents a skill that an agent can use, consisting of a name, description,
/// and a set of tool definitions.
/// </summary>
/// <param name="Name">The name of the skill.</param>
/// <param name="Description">A description of what the skill does.</param>
/// <param name="Tools">The tool definitions that make up this skill.</param>
public record Skill(string Name, string Description, IReadOnlyList<ToolDefinition> Tools);
