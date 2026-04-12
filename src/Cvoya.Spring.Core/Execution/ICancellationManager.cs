// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Coordinates conversation-level cancellation across the actor host and any delegated
/// execution containers. Implementations register a <see cref="CancellationTokenSource"/>
/// per conversation and surface it for cooperative cancellation.
/// </summary>
public interface ICancellationManager
{
    /// <summary>
    /// Register a fresh <see cref="CancellationTokenSource"/> for a conversation. If a
    /// source already exists for the conversation it is returned unchanged so callers on
    /// the same conversation observe a single cancellation signal.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <returns>The registered token source.</returns>
    CancellationTokenSource Register(string conversationId);

    /// <summary>
    /// Get the cancellation token for an existing conversation, or <see cref="CancellationToken.None"/>
    /// if none is registered. Callers that need a token source (to trigger cancellation)
    /// should use <see cref="Register"/> instead.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    /// <returns>The cancellation token for the conversation.</returns>
    CancellationToken GetToken(string conversationId);

    /// <summary>
    /// Trigger cancellation for the given conversation and propagate the signal to any
    /// delegated execution containers participating in the conversation. Safe to call
    /// when no source is registered.
    /// </summary>
    /// <param name="conversationId">The conversation to cancel.</param>
    /// <param name="cancellationToken">A token to cancel the propagation call itself.</param>
    Task CancelAsync(string conversationId, CancellationToken cancellationToken);

    /// <summary>
    /// Remove any registered source for the conversation. Called when a conversation
    /// completes normally so registrations do not accumulate.
    /// </summary>
    /// <param name="conversationId">The conversation identifier.</param>
    void Unregister(string conversationId);
}