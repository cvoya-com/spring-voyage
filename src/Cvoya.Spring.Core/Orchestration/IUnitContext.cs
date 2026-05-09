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
    /// The unit's declared AI provider id (matches
    /// <c>IAiProvider.Id</c> — e.g. <c>anthropic</c>, <c>ollama</c>).
    /// Sourced from the persisted <c>execution.provider</c> slot.
    /// <c>null</c> when the unit hasn't declared one — orchestration
    /// strategies that need a provider then fall back to whichever
    /// <c>IAiProvider</c> the DI default resolves to (#1696).
    /// </summary>
    string? ProviderId { get; }

    /// <summary>
    /// Sends a message to a specific member of the unit.
    /// </summary>
    /// <param name="message">The message to send.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>An optional response message.</returns>
    Task<Message?> SendAsync(Message message, CancellationToken cancellationToken = default);
}
