// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Microsoft.Extensions.Logging;

/// <summary>
/// Provides unit context to orchestration strategies, exposing the unit's
/// address, current members, and message-sending capability.
/// </summary>
internal sealed class UnitContext : IUnitContext
{
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="UnitContext"/> class.
    /// </summary>
    /// <param name="unitAddress">The address of the owning unit.</param>
    /// <param name="members">The current list of unit members.</param>
    /// <param name="logger">The logger instance.</param>
    public UnitContext(Address unitAddress, IReadOnlyList<Address> members, ILogger logger)
    {
        UnitAddress = unitAddress;
        Members = members;
        _logger = logger;
    }

    /// <inheritdoc />
    public Address UnitAddress { get; }

    /// <inheritdoc />
    public IReadOnlyList<Address> Members { get; }

    /// <inheritdoc />
    public Task<Message?> SendAsync(Message message, CancellationToken cancellationToken = default)
    {
        // Stub: MessageRouter integration is not yet available.
        // Log the intent and return null to indicate no immediate response.
        _logger.LogWarning(
            "UnitContext.SendAsync is a stub — message {MessageId} to {To} was not delivered. " +
            "MessageRouter integration is pending.",
            message.Id, message.To);

        return Task.FromResult<Message?>(null);
    }
}
