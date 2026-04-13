// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

/// <summary>
/// Dapr actor interface for human actors. Humans share the
/// <see cref="IAgent"/> mailbox / message-dispatch contract so the router
/// can deliver messages to humans the same way it delivers to agents and
/// units. In addition, humans carry identity, permissions, and
/// notification preferences.
/// </summary>
public interface IHumanActor : IAgent
{
    /// <summary>
    /// Gets the current global permission level of the human actor.
    /// </summary>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The current <see cref="PermissionLevel"/>.</returns>
    Task<PermissionLevel> GetPermissionAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the permission level for this human within a specific unit.
    /// </summary>
    /// <param name="unitId">The unit identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The <see cref="PermissionLevel"/> for the specified unit, or <c>null</c> if not set.</returns>
    Task<PermissionLevel?> GetPermissionForUnitAsync(string unitId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sets the permission level for this human within a specific unit.
    /// </summary>
    /// <param name="unitId">The unit identifier.</param>
    /// <param name="level">The permission level to set.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task SetPermissionForUnitAsync(string unitId, PermissionLevel level, CancellationToken cancellationToken = default);
}