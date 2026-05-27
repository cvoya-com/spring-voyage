// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Data;

using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// EF-backed implementation of <see cref="ITenantConnectorBindingRepository"/>.
/// The <see cref="SpringDbContext"/> stamps <c>TenantId</c> from the
/// ambient <c>ITenantContext</c> on insert and applies the per-entity
/// tenant query filter on read so cross-tenant access is impossible at
/// the repository layer.
/// </summary>
public class TenantConnectorBindingRepository(SpringDbContext context) : ITenantConnectorBindingRepository
{
    /// <inheritdoc />
    public async Task<TenantConnectorBinding?> GetAsync(
        string connectorSlug, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorSlug);

        var row = await context.TenantConnectorBindings
            .AsNoTracking()
            .FirstOrDefaultAsync(b => b.ConnectorSlug == connectorSlug, cancellationToken);

        return row is null
            ? null
            : new TenantConnectorBinding(row.ConnectorSlug, row.ConnectorType, row.Config);
    }

    /// <inheritdoc />
    public async Task SetAsync(
        string connectorSlug,
        Guid connectorTypeId,
        JsonElement config,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorSlug);

        var row = await context.TenantConnectorBindings
            .FirstOrDefaultAsync(b => b.ConnectorSlug == connectorSlug, cancellationToken);

        var now = DateTimeOffset.UtcNow;

        if (row is null)
        {
            // Fresh bind — synthetic Id, BoundAt = now. TenantId is
            // stamped by the SpringDbContext audit pipeline.
            context.TenantConnectorBindings.Add(new TenantConnectorBindingEntity
            {
                Id = Guid.NewGuid(),
                ConnectorSlug = connectorSlug,
                ConnectorType = connectorTypeId,
                Config = config,
                Metadata = null,
                BoundAt = now,
            });
        }
        else
        {
            // Re-bind — preserve the row id and wipe metadata: it
            // belonged to the previous install and would mislead the
            // new install's teardown path. Mirrors
            // UnitConnectorBindingRepository's rebind shape.
            row.ConnectorType = connectorTypeId;
            row.Config = config;
            row.Metadata = null;
            row.BoundAt = now;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearAsync(string connectorSlug, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorSlug);

        var row = await context.TenantConnectorBindings
            .FirstOrDefaultAsync(b => b.ConnectorSlug == connectorSlug, cancellationToken);

        if (row is null)
        {
            return;
        }

        context.TenantConnectorBindings.Remove(row);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<JsonElement?> GetMetadataAsync(
        string connectorSlug, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorSlug);

        return await context.TenantConnectorBindings
            .AsNoTracking()
            .Where(b => b.ConnectorSlug == connectorSlug)
            .Select(b => b.Metadata)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task SetMetadataAsync(
        string connectorSlug,
        JsonElement metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorSlug);

        var row = await context.TenantConnectorBindings
            .FirstOrDefaultAsync(b => b.ConnectorSlug == connectorSlug, cancellationToken);

        if (row is null)
        {
            // Runtime metadata is meaningless without a parent binding
            // — refusing keeps the schema invariant explicit (parallels
            // UnitConnectorBindingRepository.SetMetadataAsync).
            throw new InvalidOperationException(
                $"Tenant has no binding for connector '{connectorSlug}'; cannot persist runtime metadata.");
        }

        row.Metadata = metadata;
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearMetadataAsync(
        string connectorSlug, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorSlug);

        var row = await context.TenantConnectorBindings
            .FirstOrDefaultAsync(b => b.ConnectorSlug == connectorSlug, cancellationToken);

        if (row is null)
        {
            return;
        }

        row.Metadata = null;
        await context.SaveChangesAsync(cancellationToken);
    }
}
