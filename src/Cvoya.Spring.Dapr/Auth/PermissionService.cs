// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Auth;

using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.Logging;

/// <summary>
/// Resolves the effective permission for a (humanId, unitId) pair by querying the unit actor's
/// human permission state.
/// </summary>
public class PermissionService(
    IActorProxyFactory actorProxyFactory,
    ILoggerFactory loggerFactory) : IPermissionService
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<PermissionService>();

    /// <inheritdoc />
    public async Task<PermissionLevel?> ResolvePermissionAsync(
        string humanId,
        string unitId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var unitProxy = actorProxyFactory.CreateActorProxy<IUnitActor>(
                new ActorId(unitId), nameof(IUnitActor));

            return await unitProxy.GetHumanPermissionAsync(humanId, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Failed to resolve permission for human {HumanId} in unit {UnitId}",
                humanId, unitId);
            return null;
        }
    }
}