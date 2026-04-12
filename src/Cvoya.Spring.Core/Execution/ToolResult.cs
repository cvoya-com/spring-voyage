// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Represents the result of a tool invocation returned to the AI model.
/// </summary>
/// <param name="ToolUseId">The identifier of the originating <see cref="ToolCall"/>.</param>
/// <param name="Content">The textual content returned by the tool execution.</param>
/// <param name="IsError">Whether the tool execution produced an error.</param>
public record ToolResult(string ToolUseId, string Content, bool IsError = false);