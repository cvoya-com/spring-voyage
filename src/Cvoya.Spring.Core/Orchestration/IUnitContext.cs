// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Orchestration;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Provides access to unit state and members for orchestration strategies.
/// </summary>
public interface IUnitContext
{
    /// <summary>
    /// Gets the address of the unit.
    /// </summary>
    Address UnitAddress { get; }

    /// <summary>
    /// Gets the addresses of all members in the unit.
    /// </summary>
    IReadOnlyList<Address> Members { get; }

    /// <summary>
    /// Sends a message to a specific member of the unit.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An optional response message.</returns>
    Task<Message?> SendAsync(Message message, CancellationToken cancellationToken = default);
}
