// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Units;

/// <summary>
/// Minimal abstraction that the unit lifecycle endpoints invoke to bring a unit's
/// runtime container up or down. Implementations compose the underlying container
/// runtime and Dapr sidecar manager; this interface exists so the API layer can be
/// tested in isolation without reconstructing the full container toolchain.
/// </summary>
public interface IUnitContainerLifecycle
{
    /// <summary>
    /// Launches the runtime container for a unit.
    /// </summary>
    /// <param name="unitId">The unit identifier used to name the container and Dapr app id.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A task that completes when the container is running. Throws if startup fails.</returns>
    Task StartUnitAsync(string unitId, CancellationToken ct = default);

    /// <summary>
    /// Tears down the runtime container for a unit.
    /// </summary>
    /// <param name="unitId">The unit identifier whose container should be stopped.</param>
    /// <param name="ct">A token to cancel the operation.</param>
    /// <returns>A task that completes when teardown is finished. Throws if teardown fails.</returns>
    Task StopUnitAsync(string unitId, CancellationToken ct = default);
}
