// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Connectors;

using System.Diagnostics;
using System.Text.Json;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Dapr.Data;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default singleton implementation of
/// <see cref="ITenantConnectorBindingStore"/>. Creates a fresh
/// <c>IServiceScope</c> per call so the scoped
/// <see cref="ITenantConnectorBindingRepository"/> (and its
/// <c>SpringDbContext</c>) resolves cleanly from singleton call sites.
/// Mirrors <see cref="UnitConnectorBindingStore"/>.
/// </summary>
/// <remarks>
/// The bound-users discovery path (<see cref="GetBoundUsersAsync"/>)
/// extracts the bound-user list from the connector's binding config —
/// each connector knows where in its config blob the list lives. The
/// store calls
/// <see cref="ITenantBoundUserExtractor"/> to keep that decoding out of
/// the storage layer (and to avoid coupling the platform to any
/// connector's JSON shape).
/// </remarks>
public class TenantConnectorBindingStore(
    IServiceScopeFactory scopeFactory,
    ILogger<TenantConnectorBindingStore> logger) : ITenantConnectorBindingStore
{
    /// <inheritdoc />
    public async Task<TenantConnectorBinding?> GetAsync(
        string connectorSlug, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);

        var sw = Stopwatch.StartNew();
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITenantConnectorBindingRepository>();
        var result = await repo.GetAsync(connectorSlug, cancellationToken);
        sw.Stop();
        logger.LogDebug(
            "TenantConnectorBinding.Get slug={Slug} bound={Bound} elapsedMs={ElapsedMs}",
            connectorSlug, result is not null, sw.Elapsed.TotalMilliseconds);
        return result;
    }

    /// <inheritdoc />
    public async Task SetAsync(
        string connectorSlug,
        Guid connectorTypeId,
        JsonElement config,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITenantConnectorBindingRepository>();
        await repo.SetAsync(connectorSlug, connectorTypeId, config, cancellationToken);
        logger.LogInformation(
            "Tenant bound to connector {Slug} (type {TypeId})",
            connectorSlug, connectorTypeId);
    }

    /// <inheritdoc />
    public async Task ClearAsync(string connectorSlug, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITenantConnectorBindingRepository>();
        await repo.ClearAsync(connectorSlug, cancellationToken);
        logger.LogInformation("Tenant connector binding cleared (slug={Slug})", connectorSlug);
    }

    /// <inheritdoc />
    public async Task<JsonElement?> GetMetadataAsync(
        string connectorSlug, CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITenantConnectorBindingRepository>();
        var result = await repo.GetMetadataAsync(connectorSlug, cancellationToken);
        sw.Stop();
        logger.LogDebug(
            "TenantConnectorBinding.GetMetadata slug={Slug} hasMetadata={HasMetadata} elapsedMs={ElapsedMs}",
            connectorSlug, result is not null, sw.Elapsed.TotalMilliseconds);
        return result;
    }

    /// <inheritdoc />
    public async Task SetMetadataAsync(
        string connectorSlug,
        JsonElement metadata,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITenantConnectorBindingRepository>();
        await repo.SetMetadataAsync(connectorSlug, metadata, cancellationToken);
    }

    /// <inheritdoc />
    public async Task ClearMetadataAsync(
        string connectorSlug, CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITenantConnectorBindingRepository>();
        await repo.ClearMetadataAsync(connectorSlug, cancellationToken);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TenantBoundUser>> GetBoundUsersAsync(
        string connectorSlug, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectorSlug);

        await using var scope = scopeFactory.CreateAsyncScope();
        var repo = scope.ServiceProvider.GetRequiredService<ITenantConnectorBindingRepository>();
        var binding = await repo.GetAsync(connectorSlug, cancellationToken);
        if (binding is null)
        {
            return Array.Empty<TenantBoundUser>();
        }

        // Decoding the bound-user list out of the opaque config blob is a
        // per-connector concern; the platform delegates to whichever
        // extractor the connector registered. ADR-0061 §7.1: even in OSS
        // where the list has length 1, the call site iterates a list.
        var extractors = scope.ServiceProvider.GetServices<ITenantBoundUserExtractor>();
        foreach (var extractor in extractors)
        {
            if (extractor.Handles(connectorSlug))
            {
                return extractor.Extract(binding);
            }
        }

        logger.LogDebug(
            "TenantConnectorBinding.GetBoundUsers slug={Slug}: no extractor registered; returning empty list.",
            connectorSlug);
        return Array.Empty<TenantBoundUser>();
    }
}

/// <summary>
/// Connector-supplied decoder for the bound-user list embedded in a
/// tenant-binding row's opaque <c>Config</c> JSON. Each tenant-scoped
/// connector that has bound users registers one extractor with DI;
/// <see cref="TenantConnectorBindingStore.GetBoundUsersAsync"/>
/// dispatches by slug. Keeps the storage layer free of any
/// connector-specific JSON schema knowledge (ADR-0061 §7.7).
/// </summary>
public interface ITenantBoundUserExtractor
{
    /// <summary>
    /// <c>true</c> when this extractor decodes bindings whose
    /// <c>connector_slug</c> equals <paramref name="connectorSlug"/>.
    /// </summary>
    bool Handles(string connectorSlug);

    /// <summary>
    /// Decodes the bound-user list from
    /// <paramref name="binding"/>'s <c>Config</c> payload. May return
    /// an empty list when no users are bound yet.
    /// </summary>
    IReadOnlyList<TenantBoundUser> Extract(TenantConnectorBinding binding);
}
