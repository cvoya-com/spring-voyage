// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Services;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

/// <summary>
/// Default <see cref="IUnitConnectorConfigStore"/> implementation. Persists
/// connector bindings on the unit actor's state through the generic
/// <see cref="IUnitActor.SetConnectorBindingAsync"/> method. Keeping the
/// store behind an interface lets the cloud repo swap in a tenant-database
/// implementation without touching connector packages.
/// </summary>
public class UnitActorConnectorConfigStore(
    IDirectoryService directoryService,
    IActorProxyFactory actorProxyFactory) : IUnitConnectorConfigStore
{
    /// <inheritdoc />
    public async Task<UnitConnectorBinding?> GetAsync(string unitId, CancellationToken cancellationToken = default)
    {
        var proxy = await ResolveProxyAsync(unitId, cancellationToken);
        return proxy is null ? null : await proxy.GetConnectorBindingAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetAsync(string unitId, Guid typeId, JsonElement config, CancellationToken cancellationToken = default)
    {
        var proxy = await ResolveProxyAsync(unitId, cancellationToken)
            ?? throw new KeyNotFoundException($"Unit '{unitId}' not found.");
        await proxy.SetConnectorBindingAsync(new UnitConnectorBinding(typeId, config), cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearAsync(string unitId, CancellationToken cancellationToken = default)
    {
        var proxy = await ResolveProxyAsync(unitId, cancellationToken);
        if (proxy is null)
        {
            return;
        }
        await proxy.SetConnectorBindingAsync(null, cancellationToken);
    }

    private async Task<IUnitActor?> ResolveProxyAsync(string unitId, CancellationToken ct)
    {
        var address = new Address("unit", unitId);
        var entry = await directoryService.ResolveAsync(address, ct);
        if (entry is null)
        {
            return null;
        }
        return actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(entry.ActorId), nameof(UnitActor));
    }
}

/// <summary>
/// Default <see cref="IUnitConnectorRuntimeStore"/> implementation, backed
/// by the unit actor's opaque connector-metadata slot.
/// </summary>
public class UnitActorConnectorRuntimeStore(
    IDirectoryService directoryService,
    IActorProxyFactory actorProxyFactory) : IUnitConnectorRuntimeStore
{
    /// <inheritdoc />
    public async Task<JsonElement?> GetAsync(string unitId, CancellationToken cancellationToken = default)
    {
        var proxy = await ResolveProxyAsync(unitId, cancellationToken);
        return proxy is null ? null : await proxy.GetConnectorMetadataAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetAsync(string unitId, JsonElement metadata, CancellationToken cancellationToken = default)
    {
        var proxy = await ResolveProxyAsync(unitId, cancellationToken)
            ?? throw new KeyNotFoundException($"Unit '{unitId}' not found.");
        await proxy.SetConnectorMetadataAsync(metadata, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearAsync(string unitId, CancellationToken cancellationToken = default)
    {
        var proxy = await ResolveProxyAsync(unitId, cancellationToken);
        if (proxy is null)
        {
            return;
        }
        await proxy.SetConnectorMetadataAsync(null, cancellationToken);
    }

    private async Task<IUnitActor?> ResolveProxyAsync(string unitId, CancellationToken ct)
    {
        var address = new Address("unit", unitId);
        var entry = await directoryService.ResolveAsync(address, ct);
        if (entry is null)
        {
            return null;
        }
        return actorProxyFactory.CreateActorProxy<IUnitActor>(
            new ActorId(entry.ActorId), nameof(UnitActor));
    }
}