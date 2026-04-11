// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using System.Text.Json;

/// <summary>
/// Represents a tool's JSON schema definition for use within a skill.
/// </summary>
/// <param name="Name">The name of the tool.</param>
/// <param name="Description">A description of what the tool does.</param>
/// <param name="InputSchema">The JSON schema defining the tool's input parameters.</param>
public record ToolDefinition(string Name, string Description, JsonElement InputSchema);