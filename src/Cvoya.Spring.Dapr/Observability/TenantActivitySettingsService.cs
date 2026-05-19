// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// OSS implementation of <see cref="ITenantActivitySettings"/>. Reads /
/// writes one row per tenant in <c>tenant_activity_settings</c>. Issue #2492.
/// </summary>
/// <remarks>
/// <para>
/// Reads use the injected <see cref="ITenantContext"/>'s current tenant
/// to scope the query — except that the cross-tenant retention sweep
/// (<c>ActivityRetentionPurgeService</c>) needs to enumerate every
/// tenant's settings; it does so through <see cref="ITenantScopeBypass"/>
/// like every other global read. The service surface only exposes the
/// per-tenant lookup; the bypass is the sweep's responsibility, not
/// ours.
/// </para>
/// <para>
/// The cloud overlay may register a decorated implementation that adds
/// role checks or sources values from a different store; this service
/// stays a thin EF Core wrapper so the overlay never needs to peer
/// into a persistence type.
/// </para>
/// </remarks>
public class TenantActivitySettingsService(
    SpringDbContext dbContext,
    TimeProvider timeProvider) : ITenantActivitySettings
{
    /// <inheritdoc />
    public async Task<TenantActivitySettingsSnapshot> GetAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        // Direct AsNoTracking lookup; the table has no soft-delete and
        // no per-row tenant query filter (it's keyed on TenantId), so
        // we can read across tenants from anywhere that supplies the
        // id. Callers that need to enforce "current tenant only" do
        // that at the API gate.
        var row = await dbContext.TenantActivitySettings
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

        return row is null
            ? new TenantActivitySettingsSnapshot(
                tenantId,
                ITenantActivitySettings.DefaultLevel,
                ITenantActivitySettings.DefaultRetentionDays)
            : new TenantActivitySettingsSnapshot(
                tenantId,
                ParseLevel(row.Level),
                row.RetentionDays);
    }

    /// <inheritdoc />
    public async Task<TenantActivitySettingsSnapshot> SetAsync(
        Guid tenantId,
        ActivityCaptureLevel? level,
        int? retentionDays,
        CancellationToken cancellationToken = default)
    {
        if (retentionDays is { } days && days <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionDays), days,
                "Activity retention horizon must be positive.");
        }

        var now = timeProvider.GetUtcNow();
        var row = await dbContext.TenantActivitySettings
            .FirstOrDefaultAsync(s => s.TenantId == tenantId, cancellationToken);

        if (row is null)
        {
            row = new TenantActivitySettingsEntity
            {
                TenantId = tenantId,
                Level = (level ?? ITenantActivitySettings.DefaultLevel).ToString(),
                RetentionDays = retentionDays ?? ITenantActivitySettings.DefaultRetentionDays,
                CreatedAt = now,
                UpdatedAt = now,
            };
            dbContext.TenantActivitySettings.Add(row);
        }
        else
        {
            if (level.HasValue)
            {
                row.Level = level.Value.ToString();
            }
            if (retentionDays.HasValue)
            {
                row.RetentionDays = retentionDays.Value;
            }
            row.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return new TenantActivitySettingsSnapshot(
            tenantId,
            ParseLevel(row.Level),
            row.RetentionDays);
    }

    private static ActivityCaptureLevel ParseLevel(string raw)
        => Enum.TryParse<ActivityCaptureLevel>(raw, ignoreCase: true, out var value)
            ? value
            : ITenantActivitySettings.DefaultLevel;
}
