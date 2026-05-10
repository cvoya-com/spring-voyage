// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF-backed implementation of <see cref="IUnitConnectorBindingRepository"/>.
/// The <see cref="SpringDbContext"/> stamps <c>TenantId</c> from the
/// ambient <c>ITenantContext</c> on insert and applies the per-entity
/// tenant query filter on read so cross-tenant access is impossible at
/// the repository layer.
/// </summary>
public class UnitConnectorBindingRepository(SpringDbContext context) : IUnitConnectorBindingRepository
{
    /// <inheritdoc />
    public async Task<UnitConnectorBinding?> GetAsync(
        Guid unitId, CancellationToken cancellationToken = default)
    {
        var row = await context.UnitConnectorBindings
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.UnitId == unitId, cancellationToken);

        return row is null
            ? null
            : new UnitConnectorBinding(row.ConnectorType, row.Config);
    }

    /// <inheritdoc />
    public async Task SetAsync(
        Guid unitId,
        Guid connectorTypeId,
        JsonElement config,
        CancellationToken cancellationToken = default)
    {
        var row = await context.UnitConnectorBindings
            .FirstOrDefaultAsync(b => b.UnitId == unitId, cancellationToken);

        var now = DateTimeOffset.UtcNow;

        if (row is null)
        {
            // Fresh bind — synthetic Id, BoundAt = now. TenantId is
            // stamped by the SpringDbContext audit pipeline.
            context.UnitConnectorBindings.Add(new UnitConnectorBindingEntity
            {
                Id = Guid.NewGuid(),
                UnitId = unitId,
                ConnectorType = connectorTypeId,
                Config = config,
                Metadata = null,
                BoundAt = now,
            });
        }
        else
        {
            // Re-bind — preserve the row id (so any future joins on
            // binding identity remain stable) but reset BoundAt to
            // reflect the new lifetime, and wipe metadata: it belonged
            // to the previous connector type and would mislead the new
            // connector's teardown path.
            row.ConnectorType = connectorTypeId;
            row.Config = config;
            row.Metadata = null;
            row.BoundAt = now;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        var row = await context.UnitConnectorBindings
            .FirstOrDefaultAsync(b => b.UnitId == unitId, cancellationToken);

        if (row is null)
        {
            return;
        }

        context.UnitConnectorBindings.Remove(row);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<JsonElement?> GetMetadataAsync(
        Guid unitId, CancellationToken cancellationToken = default)
    {
        return await context.UnitConnectorBindings
            .AsNoTracking()
            .Where(b => b.UnitId == unitId)
            .Select(b => b.Metadata)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetMetadataAsync(
        Guid unitId,
        JsonElement metadata,
        CancellationToken cancellationToken = default)
    {
        var row = await context.UnitConnectorBindings
            .FirstOrDefaultAsync(b => b.UnitId == unitId, cancellationToken);

        if (row is null)
        {
            // Runtime metadata is meaningless without a parent binding
            // — refusing keeps the schema invariant explicit.
            throw new InvalidOperationException(
                $"Unit '{unitId}' has no connector binding; cannot persist runtime metadata.");
        }

        row.Metadata = metadata;
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearMetadataAsync(Guid unitId, CancellationToken cancellationToken = default)
    {
        var row = await context.UnitConnectorBindings
            .FirstOrDefaultAsync(b => b.UnitId == unitId, cancellationToken);

        if (row is null)
        {
            return;
        }

        row.Metadata = null;
        await context.SaveChangesAsync(cancellationToken);
    }
}
