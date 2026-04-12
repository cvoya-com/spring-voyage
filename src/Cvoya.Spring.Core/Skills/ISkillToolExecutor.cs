// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Skills;

using Cvoya.Spring.Core.Execution;

/// <summary>
/// Dispatches connector-owned tool invocations. Connectors register one or more
/// implementations with DI; the execution dispatcher resolves them as an
/// <see cref="IEnumerable{T}"/> and routes tool calls to the first executor
/// that reports <see cref="CanHandle"/> for the tool name.
/// </summary>
public interface ISkillToolExecutor
{
    /// <summary>
    /// Returns whether this executor can handle a tool call with the given name.
    /// </summary>
    /// <param name="toolName">The tool name reported by the AI model.</param>
    /// <returns><c>true</c> if the executor can dispatch this tool; otherwise, <c>false</c>.</returns>
    bool CanHandle(string toolName);

    /// <summary>
    /// Executes the tool call and returns the result to be sent back to the model.
    /// </summary>
    /// <param name="call">The tool call requested by the model.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The result of the tool invocation.</returns>
    Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken);
}