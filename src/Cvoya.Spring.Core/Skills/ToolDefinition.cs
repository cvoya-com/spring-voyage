/*
 * Copyright CVOYA LLC.
 *
 * This source code is proprietary and confidential.
 * Unauthorized copying, modification, distribution, or use of this file,
 * via any medium, is strictly prohibited without the prior written consent of CVOYA LLC.
 */

namespace Cvoya.Spring.Core.Skills;

using System.Text.Json;

/// <summary>
/// Represents a tool's JSON schema definition for use within a skill.
/// </summary>
/// <param name="Name">The name of the tool.</param>
/// <param name="Description">A description of what the tool does.</param>
/// <param name="InputSchema">The JSON schema defining the tool's input parameters.</param>
public record ToolDefinition(string Name, string Description, JsonElement InputSchema);
