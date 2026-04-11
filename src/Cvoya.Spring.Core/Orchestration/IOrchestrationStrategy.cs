// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Orchestration;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Defines a strategy for orchestrating message routing within a unit.
/// </summary>
public interface IOrchestrationStrategy
{
    /// <summary>
    /// Orchestrates the handling of an incoming message within a unit context.
    /// </summary>
    /// <param name="message">The incoming message to orchestrate.</param>
    /// <param name="context">The unit context providing state and member access.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An optional response message.</returns>
    Task<Message?> OrchestrateAsync(Message message, IUnitContext context, CancellationToken cancellationToken = default);
}