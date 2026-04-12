// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using Cvoya.Spring.Core;

/// <summary>
/// Thrown by <see cref="ISkillRegistry"/> when an invoked tool name does not
/// exist in that registry. Surfaced to MCP clients as a JSON-RPC "method not found".
/// </summary>
public class SkillNotFoundException : SpringException
{
    /// <inheritdoc />
    public SkillNotFoundException(string toolName)
        : base($"Skill tool '{toolName}' is not registered.")
    {
        ToolName = toolName;
    }

    /// <summary>The tool name that was not found.</summary>
    public string ToolName { get; }
}