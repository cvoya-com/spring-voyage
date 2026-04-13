// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

/// <summary>
/// Dapr actor interface for connector actors. Connectors bridge external
/// systems (e.g., GitHub, Slack) into the Spring Voyage platform. They share
/// the <see cref="IAgent"/> mailbox / message-dispatch contract so the
/// router can deliver messages to them the same way it delivers to agents
/// and units.
/// </summary>
public interface IConnectorActor : IAgent
{
    /// <summary>
    /// Gets the current connection status of the connector.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The current <see cref="ConnectionStatus"/>.</returns>
    Task<ConnectionStatus> GetConnectionStatusAsync(CancellationToken cancellationToken = default);
}