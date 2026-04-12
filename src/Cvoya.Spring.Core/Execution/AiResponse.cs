// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Structured response from an <see cref="IAiProvider"/> that may include text, tool calls, or both.
/// </summary>
/// <param name="Text">The assistant's plain-text output, if any.</param>
/// <param name="ToolCalls">Tool invocations requested by the model.</param>
/// <param name="StopReason">The reason the model stopped generating (e.g. "end_turn", "tool_use").</param>
public record AiResponse(string? Text, IReadOnlyList<ToolCall> ToolCalls, string StopReason);