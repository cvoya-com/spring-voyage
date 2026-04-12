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
}