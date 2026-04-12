// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

using Cvoya.Spring.Core.Skills;

/// <summary>
/// Provides an abstraction over AI model interactions.
/// </summary>
public interface IAiProvider
{
    /// <summary>
    /// Sends a prompt to the AI model and returns the response.
    /// </summary>
    /// <param name="prompt">The prompt to send to the AI model.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The AI model's response.</returns>
    Task<string> CompleteAsync(string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a prompt to the AI model and returns a stream of events as they are generated.
    /// </summary>
    /// <param name="prompt">The prompt to send to the AI model.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An asynchronous stream of <see cref="StreamEvent"/> instances.</returns>
    IAsyncEnumerable<StreamEvent> StreamCompleteAsync(string prompt, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a multi-turn conversation with tool definitions to the AI model and returns
    /// a structured response that may include tool invocations. The default implementation
    /// throws <see cref="NotSupportedException"/>; providers that support tool use should
    /// override it.
    /// </summary>
    /// <param name="turns">The ordered conversation turns sent to the model.</param>
    /// <param name="tools">The tools the model is permitted to call.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The structured AI response.</returns>
    Task<AiResponse> CompleteWithToolsAsync(
        IReadOnlyList<ConversationTurn> turns,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} does not support tool use.");

    /// <summary>
    /// Streams a multi-turn tool-aware conversation from the AI model. Emits the same
    /// <see cref="StreamEvent"/> shapes as <see cref="StreamCompleteAsync"/> (so text deltas
    /// continue to flow to users), plus <see cref="StreamEvent.ToolUseComplete"/> once each
    /// tool-use content block finishes assembling. Providers that support tool use should
    /// override this; the default implementation throws <see cref="NotSupportedException"/>.
    /// </summary>
    /// <param name="turns">The ordered conversation turns sent to the model.</param>
    /// <param name="tools">The tools the model is permitted to call.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An asynchronous stream of <see cref="StreamEvent"/> instances.</returns>
    IAsyncEnumerable<StreamEvent> StreamCompleteWithToolsAsync(
        IReadOnlyList<ConversationTurn> turns,
        IReadOnlyList<ToolDefinition> tools,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException($"{GetType().Name} does not support streaming tool use.");
}