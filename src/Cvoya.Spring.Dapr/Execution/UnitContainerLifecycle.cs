// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Core.Units;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Default <see cref="IUnitContainerLifecycle"/> implementation that adapts <see cref="ContainerLifecycleManager"/>
/// into the simpler start/stop surface used by the unit API.
/// </summary>
/// <remarks>
/// Container/sidecar/network identifiers produced by <see cref="StartUnitAsync"/> are
/// persisted to the Dapr state store under <c>"Unit:ContainerHandle:{unitId}"</c> so a
/// subsequent <see cref="StopUnitAsync"/> call can tear them down even after an API-host
/// restart. Using one state-store entry per unit (rather than a single map) avoids the
/// read-modify-write race that would occur when two units start concurrently.
/// </remarks>
public class UnitContainerLifecycle(
    ContainerLifecycleManager lifecycleManager,
    IStateStore stateStore,
    IOptions<UnitRuntimeOptions> options,
    ILoggerFactory loggerFactory) : IUnitContainerLifecycle
{
    private const string HandleKeyPrefix = "Unit:ContainerHandle:";

    private readonly ILogger<UnitContainerLifecycle> _logger = loggerFactory.CreateLogger<UnitContainerLifecycle>();
    private readonly UnitRuntimeOptions _options = options.Value;

    /// <inheritdoc />
    public async Task StartUnitAsync(string unitId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);

        var appId = BuildAppId(unitId);

        var config = new ContainerConfig(
            Image: _options.Image,
            DaprEnabled: true,
            DaprAppId: appId,
            DaprAppPort: _options.AppPort,
            Labels: new Dictionary<string, string>
            {
                ["spring.unit.id"] = unitId,
            });

        _logger.LogInformation(
            "Launching container for unit {UnitId} with app-id {AppId} using image {Image}",
            unitId, appId, _options.Image);

        var result = await lifecycleManager.LaunchWithSidecarAsync(config, ct);

        var handle = new UnitLifecycleHandle(
            result.ContainerResult.ContainerId,
            result.SidecarInfo.SidecarId,
            result.NetworkName);

        await stateStore.SetAsync(HandleKey(unitId), handle, ct);
    }

    /// <inheritdoc />
    public async Task StopUnitAsync(string unitId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);

        var key = HandleKey(unitId);
        var handle = await stateStore.GetAsync<UnitLifecycleHandle>(key, ct);

        if (handle is null)
        {
            _logger.LogWarning(
                "No lifecycle handle tracked for unit {UnitId}; issuing teardown with null identifiers.",
                unitId);
            handle = new UnitLifecycleHandle(null, null, null);
        }

        await lifecycleManager.TeardownAsync(
            handle.ContainerId,
            handle.SidecarId,
            handle.NetworkName,
            ct);

        // Clear the handle after a successful teardown so a subsequent restart starts clean.
        await stateStore.DeleteAsync(key, ct);
    }

    private static string BuildAppId(string unitId)
    {
        var raw = $"spring-unit-{unitId}";
        return raw.Length > 32 ? raw[..32] : raw;
    }

    private static string HandleKey(string unitId) => HandleKeyPrefix + unitId;

    /// <summary>
    /// Persistable record of the container, sidecar, and network identifiers produced
    /// by a successful <see cref="StartUnitAsync"/>. Nullability accommodates partial
    /// launches and legacy entries.
    /// </summary>
    /// <param name="ContainerId">The application container identifier, or <c>null</c>.</param>
    /// <param name="SidecarId">The Dapr sidecar container identifier, or <c>null</c>.</param>
    /// <param name="NetworkName">The container network name, or <c>null</c>.</param>
    public sealed record UnitLifecycleHandle(string? ContainerId, string? SidecarId, string? NetworkName);
}
