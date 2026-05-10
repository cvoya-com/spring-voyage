// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Connectors;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Default <see cref="IUnitConnectorConfigStore"/> implementation. Per
/// ADR-0040 / #2050 connector bindings live in the
/// <c>unit_connector_bindings</c> EF table, not on the unit actor; this
/// adapter forwards every call to <see cref="IUnitConnectorBindingStore"/>
/// after resolving the connector package's <c>unitId</c> argument
/// (a directory path like <c>units/engineering</c> or the unit's
/// canonical actor Guid string) to the unit's stable Guid identity via
/// <see cref="IDirectoryService"/>.
/// </summary>
/// <remarks>
/// Keeping the public <see cref="IUnitConnectorConfigStore"/> interface
/// shape unchanged means connector packages built against the v0.1 OSS
/// surface (GitHub, WebSearch, Arxiv) keep working — only the
/// implementation moves from actor-state to EF.
/// </remarks>
public class UnitActorConnectorConfigStore(
    IDirectoryService directoryService,
    IUnitConnectorBindingStore bindingStore) : IUnitConnectorConfigStore
{
    /// <inheritdoc />
    public async Task<UnitConnectorBinding?> GetAsync(string unitId, CancellationToken cancellationToken = default)
    {
        var unitGuid = await ResolveUnitGuidAsync(unitId, cancellationToken);
        if (unitGuid is null)
        {
            return null;
        }
        return await bindingStore.GetAsync(unitGuid.Value, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetAsync(string unitId, Guid typeId, JsonElement config, CancellationToken cancellationToken = default)
    {
        var unitGuid = await ResolveUnitGuidAsync(unitId, cancellationToken)
            ?? throw new KeyNotFoundException($"Unit '{unitId}' not found.");
        await bindingStore.SetAsync(unitGuid, typeId, config, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearAsync(string unitId, CancellationToken cancellationToken = default)
    {
        var unitGuid = await ResolveUnitGuidAsync(unitId, cancellationToken);
        if (unitGuid is null)
        {
            return;
        }
        await bindingStore.ClearAsync(unitGuid.Value, cancellationToken);
    }

    /// <summary>
    /// Resolves the connector-package-provided <paramref name="unitId"/>
    /// (path or Guid string) to the unit's stable Guid identity. Returns
    /// <c>null</c> when no directory entry exists. Tries
    /// <see cref="IDirectoryService.ResolveAsync"/> first so the
    /// path-based form keeps working; falls back to a direct
    /// <see cref="GuidFormatter.TryParse"/> when the directory entry has
    /// not been registered yet (e.g. first-bind during unit creation).
    /// </summary>
    private async Task<Guid?> ResolveUnitGuidAsync(string unitId, CancellationToken ct)
    {
        var address = Address.For("unit", unitId);
        var entry = await directoryService.ResolveAsync(address, ct);
        if (entry is not null)
        {
            return entry.ActorId;
        }
        return GuidFormatter.TryParse(unitId, out var direct) ? direct : null;
    }
}

/// <summary>
/// Default <see cref="IUnitConnectorRuntimeStore"/> implementation. Per
/// ADR-0040 / #2050 the runtime metadata column lives on the same
/// <c>unit_connector_bindings</c> row as the binding itself, so this
/// adapter forwards every call to <see cref="IUnitConnectorBindingStore"/>.
/// </summary>
public class UnitActorConnectorRuntimeStore(
    IDirectoryService directoryService,
    IUnitConnectorBindingStore bindingStore) : IUnitConnectorRuntimeStore
{
    /// <inheritdoc />
    public async Task<JsonElement?> GetAsync(string unitId, CancellationToken cancellationToken = default)
    {
        var unitGuid = await ResolveUnitGuidAsync(unitId, cancellationToken);
        if (unitGuid is null)
        {
            return null;
        }
        return await bindingStore.GetMetadataAsync(unitGuid.Value, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetAsync(string unitId, JsonElement metadata, CancellationToken cancellationToken = default)
    {
        var unitGuid = await ResolveUnitGuidAsync(unitId, cancellationToken)
            ?? throw new KeyNotFoundException($"Unit '{unitId}' not found.");
        await bindingStore.SetMetadataAsync(unitGuid, metadata, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearAsync(string unitId, CancellationToken cancellationToken = default)
    {
        var unitGuid = await ResolveUnitGuidAsync(unitId, cancellationToken);
        if (unitGuid is null)
        {
            return;
        }
        await bindingStore.ClearMetadataAsync(unitGuid.Value, cancellationToken);
    }

    private async Task<Guid?> ResolveUnitGuidAsync(string unitId, CancellationToken ct)
    {
        var address = Address.For("unit", unitId);
        var entry = await directoryService.ResolveAsync(address, ct);
        if (entry is not null)
        {
            return entry.ActorId;
        }
        return GuidFormatter.TryParse(unitId, out var direct) ? direct : null;
    }
}
