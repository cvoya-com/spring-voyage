// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Messaging;

/// <summary>
/// Resolves <see cref="Address"/> instances to their destination and delivers messages.
/// Extracted so callers (endpoints, actors, tools) can depend on the abstraction and
/// substitute a test double without spinning up the Dapr-backed implementation.
/// </summary>
public interface IMessageRouter
{
    /// <summary>
    /// Routes a message to its destination and returns the response.
    /// </summary>
    /// <param name="message">The message to route.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>A result containing the recipient's response or a routing error.</returns>
    Task<Result<Message?, RoutingError>> RouteAsync(Message message, CancellationToken cancellationToken = default);
}
