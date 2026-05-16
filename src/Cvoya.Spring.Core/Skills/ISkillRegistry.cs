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
    /// Returns the tool definitions whose canonical <see cref="ToolDefinition.Name"/>
    /// starts with the supplied namespace segment (the portion before the first
    /// <c>.</c> in a <c>&lt;namespace&gt;.&lt;tool_name&gt;</c> id). The default
    /// implementation filters <see cref="GetToolDefinitions"/>; registries that
    /// already partition their tool surface by namespace MAY override with a
    /// more efficient implementation.
    /// </summary>
    /// <remarks>
    /// Consumed by the grant pipeline (#2335) so a unit can authorise an entire
    /// namespace (e.g. all <c>github.*</c> tools) without enumerating each id.
    /// Matching is case-sensitive and exact — callers must pass the lower-case
    /// namespace segment as it appears on the tool ids.
    /// </remarks>
    /// <param name="namespace">
    /// Namespace segment to match (e.g. <c>"github"</c>, <c>"sv"</c>).
    /// </param>
    IReadOnlyList<ToolDefinition> GetToolsByNamespace(string @namespace)
    {
        if (string.IsNullOrEmpty(@namespace))
        {
            return Array.Empty<ToolDefinition>();
        }
        var tools = GetToolDefinitions();
        if (tools.Count == 0)
        {
            return Array.Empty<ToolDefinition>();
        }
        var matched = new List<ToolDefinition>(tools.Count);
        foreach (var tool in tools)
        {
            if (string.Equals(tool.Namespace, @namespace, StringComparison.Ordinal))
            {
                matched.Add(tool);
            }
        }
        return matched;
    }

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

    /// <summary>
    /// Caller-aware variant invoked by the MCP server with the active session's
    /// caller identity (see <see cref="ToolCallContext"/>). Tools that need to
    /// answer questions about <em>who</em> is calling them (e.g. "list members
    /// of my unit") override this. The default implementation drops the context
    /// and delegates to the no-context <see cref="InvokeAsync(string, JsonElement, CancellationToken)"/>
    /// overload so existing skills authored before the context seam landed
    /// keep working unchanged.
    /// </summary>
    /// <param name="toolName">The tool name, as advertised by <see cref="GetToolDefinitions"/>.</param>
    /// <param name="arguments">The tool arguments as a JSON object.</param>
    /// <param name="context">Caller identity resolved from the active MCP session.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<JsonElement> InvokeAsync(string toolName, JsonElement arguments, ToolCallContext context, CancellationToken cancellationToken = default)
        => InvokeAsync(toolName, arguments, cancellationToken);
}
