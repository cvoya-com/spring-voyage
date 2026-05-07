// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.AgentRuntimes;

using System.Text.Json;

using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default EF Core-backed implementation of
/// <see cref="ITenantAgentRuntimeInstallService"/>. Persists rows to
/// <c>tenant_model_provider_installs</c> (renamed under ADR-0038 — see
/// <see cref="TenantModelProviderInstallEntity"/>) and materialises
/// config from the runtime's seed defaults when none is supplied on
/// install.
/// </summary>
/// <remarks>
/// PR-1a transitional shape: the public interface still surfaces
/// <c>RuntimeId</c> on every read/write while the wire DTOs remain
/// runtime-keyed; the persisted column is named <c>provider_id</c>
/// because PR-1b reshapes the wire to be provider-keyed. The runtime
/// id flows verbatim into the column today; the wire reshape is the
/// follow-on PR.
/// </remarks>
public sealed class TenantAgentRuntimeInstallService(
    SpringDbContext dbContext,
    ITenantContext tenantContext,
    IAgentRuntimeRegistry runtimeRegistry,
    ILogger<TenantAgentRuntimeInstallService> logger) : ITenantAgentRuntimeInstallService
{
    /// <inheritdoc />
    public async Task<InstalledAgentRuntime> InstallAsync(
        string runtimeId,
        AgentRuntimeInstallConfig? config,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeId);

        var runtime = runtimeRegistry.Get(runtimeId)
            ?? throw new InvalidOperationException(
                $"Agent runtime '{runtimeId}' is not registered with the host.");

        var tenantId = tenantContext.CurrentTenantId;
        var now = DateTimeOffset.UtcNow;

        // IgnoreQueryFilters so we can revive a soft-deleted row instead
        // of leaving it orphaned and inserting a duplicate.
        var existing = await dbContext.TenantModelProviderInstalls
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                e => e.TenantId == tenantId && e.ProviderId == runtime.Id,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            var resolved = config ?? AgentRuntimeInstallConfig.FromRuntimeDefaults(runtime);
            var entity = new TenantModelProviderInstallEntity
            {
                TenantId = tenantId,
                ProviderId = runtime.Id,
                ConfigJson = Serialize(resolved),
                InstalledAt = now,
                UpdatedAt = now,
            };
            dbContext.TenantModelProviderInstalls.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Installed agent runtime '{RuntimeId}' on tenant '{TenantId}'.",
                runtime.Id, tenantId);
            return Project(entity, resolved);
        }

        if (existing.DeletedAt is not null)
        {
            // Revive a previously uninstalled row. Treat as a fresh install
            // so InstalledAt reflects the resurrection.
            var resolved = config ?? AgentRuntimeInstallConfig.FromRuntimeDefaults(runtime);
            existing.DeletedAt = null;
            existing.InstalledAt = now;
            existing.ConfigJson = Serialize(resolved);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Re-installed agent runtime '{RuntimeId}' on tenant '{TenantId}' (was previously uninstalled).",
                runtime.Id, tenantId);
            return Project(existing, resolved);
        }

        // Idempotent re-install. Preserve existing config unless the caller
        // explicitly supplied one — matches the ITenantSeedProvider rule
        // that repeat invocations must not overwrite operator edits.
        var effective = config is null
            ? Deserialize(existing.ConfigJson) ?? AgentRuntimeInstallConfig.Empty
            : config;
        if (config is not null)
        {
            existing.ConfigJson = Serialize(effective);
        }
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogDebug(
            "Agent runtime '{RuntimeId}' was already installed on tenant '{TenantId}'; refreshed UpdatedAt.",
            runtime.Id, tenantId);
        return Project(existing, effective);
    }

    /// <inheritdoc />
    public async Task UninstallAsync(string runtimeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeId);

        var tenantId = tenantContext.CurrentTenantId;
        var existing = await dbContext.TenantModelProviderInstalls
            .FirstOrDefaultAsync(
                e => e.TenantId == tenantId && e.ProviderId == runtimeId,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            return;
        }

        existing.DeletedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation(
            "Uninstalled agent runtime '{RuntimeId}' from tenant '{TenantId}'.",
            runtimeId, tenantId);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<InstalledAgentRuntime>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.TenantModelProviderInstalls
            .OrderBy(e => e.ProviderId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(r => Project(r, Deserialize(r.ConfigJson) ?? AgentRuntimeInstallConfig.Empty)).ToArray();
    }

    /// <inheritdoc />
    public async Task<InstalledAgentRuntime?> GetAsync(string runtimeId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeId);

        var row = await dbContext.TenantModelProviderInstalls
            .FirstOrDefaultAsync(
                e => e.ProviderId == runtimeId,
                cancellationToken)
            .ConfigureAwait(false);
        return row is null
            ? null
            : Project(row, Deserialize(row.ConfigJson) ?? AgentRuntimeInstallConfig.Empty);
    }

    /// <inheritdoc />
    public async Task<InstalledAgentRuntime> UpdateConfigAsync(
        string runtimeId,
        AgentRuntimeInstallConfig config,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeId);
        ArgumentNullException.ThrowIfNull(config);

        var tenantId = tenantContext.CurrentTenantId;
        var row = await dbContext.TenantModelProviderInstalls
            .FirstOrDefaultAsync(
                e => e.ProviderId == runtimeId,
                cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Agent runtime '{runtimeId}' is not installed on tenant '{tenantId}'.");

        row.ConfigJson = Serialize(config);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return Project(row, config);
    }

    private static InstalledAgentRuntime Project(
        TenantModelProviderInstallEntity row,
        AgentRuntimeInstallConfig config)
        => new(row.ProviderId, row.TenantId, config, row.InstalledAt, row.UpdatedAt);

    private static JsonElement? Serialize(AgentRuntimeInstallConfig config)
        => JsonSerializer.SerializeToElement(config);

    private static AgentRuntimeInstallConfig? Deserialize(JsonElement? element)
    {
        if (element is null || element.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        return JsonSerializer.Deserialize<AgentRuntimeInstallConfig>(element.Value.GetRawText());
    }
}