// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.ModelProviders;

using System.Text.Json;

using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default EF Core-backed implementation of
/// <see cref="ITenantModelProviderInstallService"/>. Persists rows to
/// <c>tenant_model_provider_installs</c> (see
/// <see cref="TenantModelProviderInstallEntity"/>) and materialises
/// config from the provider's catalogue defaults when none is supplied
/// on install.
/// </summary>
public sealed class TenantModelProviderInstallService(
    SpringDbContext dbContext,
    ITenantContext tenantContext,
    IRuntimeCatalog runtimeCatalog,
    ILogger<TenantModelProviderInstallService> logger) : ITenantModelProviderInstallService
{
    /// <inheritdoc />
    public async Task<InstalledModelProvider> InstallAsync(
        string providerId,
        ModelProviderInstallConfig? config,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        // ADR-0038: installs are keyed on model-provider id, sourced from
        // the runtime catalogue (platform/runtime-catalog.yaml).
        var provider = runtimeCatalog.GetModelProvider(providerId)
            ?? throw new InvalidOperationException(
                $"Model provider '{providerId}' is not declared in platform/runtime-catalog.yaml.");

        var tenantId = tenantContext.CurrentTenantId;
        var now = DateTimeOffset.UtcNow;

        // IgnoreQueryFilters so we can revive a soft-deleted row instead
        // of leaving it orphaned and inserting a duplicate.
        var existing = await dbContext.TenantModelProviderInstalls
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                e => e.TenantId == tenantId && e.ProviderId == provider.Id,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            var resolved = config ?? FromCatalogueDefaults(provider);
            var entity = new TenantModelProviderInstallEntity
            {
                TenantId = tenantId,
                ProviderId = provider.Id,
                ConfigJson = Serialize(resolved),
                InstalledAt = now,
                UpdatedAt = now,
            };
            dbContext.TenantModelProviderInstalls.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Installed model provider '{ProviderId}' on tenant '{TenantId}'.",
                provider.Id, tenantId);
            return Project(entity, resolved);
        }

        if (existing.DeletedAt is not null)
        {
            var resolved = config ?? FromCatalogueDefaults(provider);
            existing.DeletedAt = null;
            existing.InstalledAt = now;
            existing.ConfigJson = Serialize(resolved);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Re-installed model provider '{ProviderId}' on tenant '{TenantId}' (was previously uninstalled).",
                provider.Id, tenantId);
            return Project(existing, resolved);
        }

        // Idempotent re-install. Preserve existing config unless the caller
        // explicitly supplied one — matches the ITenantSeedProvider rule
        // that repeat invocations must not overwrite operator edits.
        var effective = config is null
            ? Deserialize(existing.ConfigJson) ?? ModelProviderInstallConfig.Empty
            : config;
        if (config is not null)
        {
            existing.ConfigJson = Serialize(effective);
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogDebug(
            "Model provider '{ProviderId}' was already installed on tenant '{TenantId}'; refreshed UpdatedAt.",
            provider.Id, tenantId);
        return Project(existing, effective);
    }

    /// <inheritdoc />
    public async Task UninstallAsync(string providerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var tenantId = tenantContext.CurrentTenantId;
        var existing = await dbContext.TenantModelProviderInstalls
            .FirstOrDefaultAsync(
                e => e.TenantId == tenantId && e.ProviderId == providerId,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return;
        }

        existing.DeletedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Uninstalled model provider '{ProviderId}' from tenant '{TenantId}'.",
            providerId, tenantId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstalledModelProvider>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.TenantModelProviderInstalls
            .OrderBy(e => e.ProviderId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(r => Project(r, Deserialize(r.ConfigJson) ?? ModelProviderInstallConfig.Empty)).ToArray();
    }

    /// <inheritdoc />
    public async Task<InstalledModelProvider?> GetAsync(string providerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);

        var row = await dbContext.TenantModelProviderInstalls
            .FirstOrDefaultAsync(
                e => e.ProviderId == providerId,
                cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? null
            : Project(row, Deserialize(row.ConfigJson) ?? ModelProviderInstallConfig.Empty);
    }

    /// <inheritdoc />
    public async Task<InstalledModelProvider> UpdateConfigAsync(
        string providerId,
        ModelProviderInstallConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        ArgumentNullException.ThrowIfNull(config);

        var tenantId = tenantContext.CurrentTenantId;
        var row = await dbContext.TenantModelProviderInstalls
            .FirstOrDefaultAsync(
                e => e.ProviderId == providerId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Model provider '{providerId}' is not installed on tenant '{tenantId}'.");

        row.ConfigJson = Serialize(config);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Project(row, config);
    }

    private static ModelProviderInstallConfig FromCatalogueDefaults(ModelProvider provider)
    {
        var models = provider.DefaultModels.ToArray();
        return new ModelProviderInstallConfig(
            Models: models,
            DefaultModel: models.Length > 0 ? models[0] : null,
            BaseUrl: null);
    }

    private static InstalledModelProvider Project(
        TenantModelProviderInstallEntity row,
        ModelProviderInstallConfig config)
        => new(row.ProviderId, row.TenantId, config, row.InstalledAt, row.UpdatedAt);

    private static JsonElement? Serialize(ModelProviderInstallConfig config)
        => JsonSerializer.SerializeToElement(config);

    private static ModelProviderInstallConfig? Deserialize(JsonElement? element)
    {
        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return JsonSerializer.Deserialize<ModelProviderInstallConfig>(element.Value.GetRawText());
    }
}