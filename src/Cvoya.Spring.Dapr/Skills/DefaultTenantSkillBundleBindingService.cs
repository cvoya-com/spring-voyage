// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Skills;

using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Default EF Core-backed implementation of
/// <see cref="ITenantSkillBundleBindingService"/>. Persists rows to
/// <c>tenant_skill_bundle_bindings</c>.
/// </summary>
public sealed class DefaultTenantSkillBundleBindingService(
    SpringDbContext dbContext,
    ITenantContext tenantContext,
    ILogger<DefaultTenantSkillBundleBindingService> logger) : ITenantSkillBundleBindingService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<TenantSkillBundleBinding>> ListAsync(CancellationToken cancellationToken = default)
    {
        var rows = await dbContext.TenantSkillBundleBindings
            .OrderBy(e => e.BundleId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return rows.Select(Project).ToArray();
    }

    /// <inheritdoc />
    public async Task<TenantSkillBundleBinding?> GetAsync(string bundleId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleId);

        var row = await dbContext.TenantSkillBundleBindings
            .FirstOrDefaultAsync(e => e.BundleId == bundleId, cancellationToken)
            .ConfigureAwait(false);
        return row is null ? null : Project(row);
    }

    /// <inheritdoc />
    public async Task<TenantSkillBundleBinding> BindAsync(
        string bundleId, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleId);

        var tenantId = tenantContext.CurrentTenantId;
        var existing = await dbContext.TenantSkillBundleBindings
            .FirstOrDefaultAsync(
                e => e.TenantId == tenantId && e.BundleId == bundleId,
                cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            var entity = new TenantSkillBundleBindingEntity
            {
                TenantId = tenantId,
                BundleId = bundleId,
                Enabled = enabled,
                BoundAt = DateTimeOffset.UtcNow,
            };
            dbContext.TenantSkillBundleBindings.Add(entity);
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Skill-bundle binding: bound '{BundleId}' to tenant '{TenantId}' (enabled={Enabled}).",
                bundleId, tenantId, enabled);
            return Project(entity);
        }

        if (existing.Enabled != enabled)
        {
            existing.Enabled = enabled;
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation(
                "Skill-bundle binding: updated '{BundleId}' for tenant '{TenantId}' (enabled={Enabled}).",
                bundleId, tenantId, enabled);
        }
        else
        {
            logger.LogDebug(
                "Skill-bundle binding: '{BundleId}' already bound for tenant '{TenantId}' (enabled={Enabled}); nothing to do.",
                bundleId, tenantId, enabled);
        }
        return Project(existing);
    }

    private static TenantSkillBundleBinding Project(TenantSkillBundleBindingEntity row)
        => new(row.TenantId, row.BundleId, row.Enabled, row.BoundAt);
}