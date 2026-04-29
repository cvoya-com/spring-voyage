// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Execution;

/// <summary>
/// Coordinates thread-level cancellation across the actor host and any delegated
/// execution containers. Implementations register a <see cref="CancellationTokenSource"/>
/// per thread and surface it for cooperative cancellation.
/// </summary>
public interface ICancellationManager
{
    /// <summary>
    /// Register a fresh <see cref="CancellationTokenSource"/> for a thread. If a
    /// source already exists for the thread it is returned unchanged so callers on
    /// the same thread observe a single cancellation signal.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <returns>The registered token source.</returns>
    CancellationTokenSource Register(string threadId);

    /// <summary>
    /// Get the cancellation token for an existing thread, or <see cref="CancellationToken.None"/>
    /// if none is registered. Callers that need a token source (to trigger cancellation)
    /// should use <see cref="Register"/> instead.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    /// <returns>The cancellation token for the thread.</returns>
    CancellationToken GetToken(string threadId);

    /// <summary>
    /// Trigger cancellation for the given thread and propagate the signal to any
    /// delegated execution containers participating in the thread. Safe to call
    /// when no source is registered.
    /// </summary>
    /// <param name="threadId">The thread to cancel.</param>
    /// <param name="cancellationToken">A token to cancel the propagation call itself.</param>
    Task CancelAsync(string threadId, CancellationToken cancellationToken);

    /// <summary>
    /// Remove any registered source for the thread. Called when a thread
    /// completes normally so registrations do not accumulate.
    /// </summary>
    /// <param name="threadId">The thread identifier.</param>
    void Unregister(string threadId);
}