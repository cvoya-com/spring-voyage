// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using Cvoya.Spring.Dapr.Actors;

/// <summary>
/// Resolves the effective permission level for a human within a specific unit.
/// </summary>
public interface IPermissionService
{
    /// <summary>
    /// Resolves the effective permission level for the specified human within a unit.
    /// </summary>
    /// <param name="humanId">The human's identifier.</param>
    /// <param name="unitId">The unit's identifier.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The effective <see cref="PermissionLevel"/>, or <c>null</c> if the human has no permission in the unit.</returns>
    Task<PermissionLevel?> ResolvePermissionAsync(string humanId, string unitId, CancellationToken cancellationToken = default);
}