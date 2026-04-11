// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using Cvoya.Spring.Core.Messaging;

using global::Dapr.Actors;

/// <summary>
/// Dapr actor interface for agent actors.
/// Extends <see cref="IActor"/> and mirrors the <see cref="IMessageReceiver"/> contract
/// for use within the Dapr actor runtime.
/// </summary>
public interface IAgentActor : IActor
{
    /// <summary>
    /// Receives and processes a message, optionally returning a response.
    /// </summary>
    /// <param name="message">The message to process.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An optional response message, or <c>null</c> if no response is needed.</returns>
    Task<Message?> ReceiveAsync(Message message, CancellationToken cancellationToken = default);
}