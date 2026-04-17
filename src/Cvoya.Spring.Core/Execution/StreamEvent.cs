// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Discriminated union representing real-time streaming events from an execution environment.
/// Uses the abstract record + sealed subtypes pattern for exhaustive pattern matching.
/// </summary>
public abstract record StreamEvent(Guid Id, DateTimeOffset Timestamp)
{
    /// <summary>
    /// One or more LLM tokens generated — enables live text streaming.
    /// </summary>
    public sealed record TokenDelta(Guid Id, DateTimeOffset Timestamp, string Text)
        : StreamEvent(Id, Timestamp);

    /// <summary>
    /// Extended thinking content from the LLM (reasoning tokens).
    /// </summary>
    public sealed record ThinkingDelta(Guid Id, DateTimeOffset Timestamp, string Text)
        : StreamEvent(Id, Timestamp);

    /// <summary>
    /// A state checkpoint from the execution environment, enabling resume after failure.
    /// </summary>
    public sealed record Checkpoint(Guid Id, DateTimeOffset Timestamp, string ConversationId, string StateSnapshot)
        : StreamEvent(Id, Timestamp);

    /// <summary>
    /// Signals that the execution has completed, including final usage statistics.
    /// </summary>
    public sealed record Completed(Guid Id, DateTimeOffset Timestamp, int InputTokens, int OutputTokens, string? StopReason)
        : StreamEvent(Id, Timestamp);

    /// <summary>
    /// Signals that the execution environment is invoking a tool. Emitted
    /// before the tool runs so observers (dashboards, supervisors) see the
    /// call as it starts — the matching <see cref="ToolResult"/> arrives
    /// once the tool returns.
    /// </summary>
    /// <param name="Id">Unique identifier for this stream event.</param>
    /// <param name="Timestamp">When the tool call was dispatched.</param>
    /// <param name="CallId">Correlates the call with its <see cref="ToolResult"/>.</param>
    /// <param name="ToolName">Name of the tool or skill being invoked.</param>
    /// <param name="Arguments">Serialised arguments passed to the tool (may be empty).</param>
    public sealed record ToolCall(
        Guid Id,
        DateTimeOffset Timestamp,
        string CallId,
        string ToolName,
        string Arguments) : StreamEvent(Id, Timestamp);

    /// <summary>
    /// Signals the return from a prior <see cref="ToolCall"/> — success or
    /// failure. Observers correlate <see cref="CallId"/> back to the call
    /// event that opened the span.
    /// </summary>
    /// <param name="Id">Unique identifier for this stream event.</param>
    /// <param name="Timestamp">When the tool returned.</param>
    /// <param name="CallId">Matches the <see cref="ToolCall.CallId"/> this result belongs to.</param>
    /// <param name="ToolName">Name of the tool or skill that returned.</param>
    /// <param name="Result">Serialised tool output (may be empty on failure).</param>
    /// <param name="IsError"><c>true</c> when the tool failed.</param>
    public sealed record ToolResult(
        Guid Id,
        DateTimeOffset Timestamp,
        string CallId,
        string ToolName,
        string Result,
        bool IsError) : StreamEvent(Id, Timestamp);
}