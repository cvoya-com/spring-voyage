// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tools;

using System.Text.Json;

using Cvoya.Spring.Core;

using Microsoft.Extensions.Logging;

/// <summary>
/// Dispatches tool calls to the appropriate <see cref="Core.Tools.IPlatformTool"/> implementation.
/// Sets the <see cref="ToolExecutionContextAccessor"/> before each invocation so tools can
/// access the current agent's state manager and context.
/// </summary>
public class ToolDispatcher(
    PlatformToolRegistry registry,
    ToolExecutionContextAccessor contextAccessor,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<ToolDispatcher>();

    /// <summary>
    /// Dispatches a tool call to the registered tool with the given name.
    /// </summary>
    /// <param name="toolName">The name of the tool to invoke.</param>
    /// <param name="parameters">The tool input parameters as JSON.</param>
    /// <param name="executionContext">The execution context for this tool call.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The tool result as a JSON element.</returns>
    /// <exception cref="SpringException">Thrown when no tool with the given name is registered.</exception>
    public async Task<JsonElement> DispatchAsync(
        string toolName,
        JsonElement parameters,
        ToolExecutionContext executionContext,
        CancellationToken cancellationToken = default)
    {
        var tool = registry.Get(toolName)
            ?? throw new SpringException($"Unknown tool: {toolName}");

        _logger.LogDebug("Dispatching tool call to {ToolName}", toolName);

        contextAccessor.Current = executionContext;
        try
        {
            var contextJson = JsonSerializer.SerializeToElement(new
            {
                AgentScheme = executionContext.AgentAddress.Scheme,
                AgentPath = executionContext.AgentAddress.Path,
                executionContext.ConversationId
            });

            return await tool.ExecuteAsync(parameters, contextJson, cancellationToken);
        }
        finally
        {
            contextAccessor.Current = null;
        }
    }
}