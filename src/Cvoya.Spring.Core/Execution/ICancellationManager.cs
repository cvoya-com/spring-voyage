// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Manages cancellation tokens scoped to conversations, enabling cooperative
/// cancellation of in-progress execution work.
/// </summary>
public interface ICancellationManager
{
    /// <summary>
    /// Registers a new cancellation token source for the specified conversation.
    /// </summary>
    /// <param name="conversationId">The conversation identifier to associate with the token.</param>
    /// <param name="cancellationToken">A token to cancel the registration operation.</param>
    /// <returns>The cancellation token that will be signaled when the conversation is cancelled.</returns>
    Task<CancellationToken> RegisterAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the cancellation token for the specified conversation.
    /// </summary>
    /// <param name="conversationId">The conversation identifier to look up.</param>
    /// <param name="cancellationToken">A token to cancel the retrieval operation.</param>
    /// <returns>The cancellation token if the conversation is registered; otherwise, <see cref="CancellationToken.None"/>.</returns>
    Task<CancellationToken> GetTokenAsync(string conversationId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Triggers cancellation for the specified conversation, signaling all associated tokens.
    /// </summary>
    /// <param name="conversationId">The conversation identifier to cancel.</param>
    /// <param name="cancellationToken">A token to cancel the triggering operation.</param>
    /// <returns>A task that completes when the cancellation has been signaled.</returns>
    Task CancelAsync(string conversationId, CancellationToken cancellationToken = default);
}