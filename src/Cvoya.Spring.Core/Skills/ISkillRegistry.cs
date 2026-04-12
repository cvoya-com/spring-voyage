// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using System.Text.Json;

/// <summary>
/// Groups tool definitions from a single connector (e.g., GitHub) and executes
/// tool invocations against the connector's backing services. Implementations
/// are registered in DI as a set and consumed by the MCP server, by prompt
/// assembly, and by any future planner.
/// </summary>
public interface ISkillRegistry
{
    /// <summary>Short, lowercase identifier for this registry (e.g. <c>github</c>). Used for logging and routing.</summary>
    string Name { get; }

    /// <summary>Returns the tool definitions this registry exposes.</summary>
    IReadOnlyList<ToolDefinition> GetToolDefinitions();

    /// <summary>
    /// Executes a tool by name against the provided arguments. Arguments follow the
    /// tool's input schema. Implementations throw <see cref="SkillNotFoundException"/>
    /// when <paramref name="toolName"/> is not handled by this registry.
    /// </summary>
    /// <param name="toolName">The tool name, as advertised by <see cref="GetToolDefinitions"/>.</param>
    /// <param name="arguments">The tool arguments as a JSON object.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The tool result as a JSON element.</returns>
    Task<JsonElement> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default);
}