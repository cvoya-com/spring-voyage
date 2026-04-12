// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using System.Text.Json;

/// <summary>
/// Tagged union representing a single content block within a <see cref="ConversationTurn"/>.
/// The <see cref="Type"/> discriminator ("text", "tool_use", or "tool_result") matches the
/// Anthropic Messages API content-block shape, making serialisation straightforward.
/// </summary>
public abstract record ContentBlock(string Type)
{
    /// <summary>
    /// A plain-text block authored by either the user or the assistant.
    /// </summary>
    /// <param name="Text">The text content of the block.</param>
    public sealed record TextBlock(string Text) : ContentBlock("text");

    /// <summary>
    /// An assistant-authored block representing a tool invocation.
    /// </summary>
    /// <param name="Id">The provider-assigned identifier correlating the call with its result.</param>
    /// <param name="Name">The name of the tool to execute.</param>
    /// <param name="Input">The JSON-encoded input arguments for the tool.</param>
    public sealed record ToolUseBlock(string Id, string Name, JsonElement Input) : ContentBlock("tool_use");

    /// <summary>
    /// A user-authored block conveying the result of a previously requested tool call.
    /// </summary>
    /// <param name="ToolUseId">The identifier of the originating <see cref="ToolUseBlock"/>.</param>
    /// <param name="Content">The textual content returned by the tool execution.</param>
    /// <param name="IsError">Whether the tool execution produced an error.</param>
    public sealed record ToolResultBlock(string ToolUseId, string Content, bool IsError) : ContentBlock("tool_result");
}