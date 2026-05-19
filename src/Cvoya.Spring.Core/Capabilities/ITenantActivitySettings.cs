// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Capabilities;

/// <summary>
/// Read / write surface for the tenant's activity-capture settings —
/// the per-tenant capture level (<see cref="ActivityCaptureLevel"/>)
/// and the activity-retention horizon. Backs the
/// <c>spring tenant activity ...</c> CLI verbs and the ingest
/// controller's per-event policy check.
/// </summary>
/// <remarks>
/// <para>
/// The OSS implementation persists one row per tenant; missing rows
/// resolve to <see cref="DefaultLevel"/> / <see cref="DefaultRetentionDays"/>
/// so the OSS default tenant doesn't require an explicit seed. The cloud
/// overlay may register a decorated implementation that adds
/// permission-checks or sources values from a different store; the OSS
/// surface stays minimal so the overlay never needs to peer into a
/// persistence type.
/// </para>
/// </remarks>
public interface ITenantActivitySettings
{
    /// <summary>
    /// OSS default capture level when a tenant has not stored an
    /// explicit override. Matches issue #2492's "OSS default <c>full</c>"
    /// requirement.
    /// </summary>
    const ActivityCaptureLevel DefaultLevel = ActivityCaptureLevel.Full;

    /// <summary>
    /// OSS default retention horizon in days when a tenant has not
    /// stored an explicit override. Matches issue #2492's "30 days for
    /// OSS" requirement.
    /// </summary>
    const int DefaultRetentionDays = 30;

    /// <summary>
    /// Resolves the activity settings for <paramref name="tenantId"/>.
    /// Implementations must return <see cref="DefaultLevel"/> /
    /// <see cref="DefaultRetentionDays"/> when no row is stored for the
    /// tenant; callers never see a <c>null</c> snapshot.
    /// </summary>
    /// <param name="tenantId">The tenant to look up.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<TenantActivitySettingsSnapshot> GetAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the activity settings for <paramref name="tenantId"/>.
    /// Either field can be left as the current value by passing
    /// <c>null</c>; passing both as <c>null</c> is a no-op.
    /// </summary>
    /// <param name="tenantId">The tenant whose settings change.</param>
    /// <param name="level">New capture level, or <c>null</c> to leave unchanged.</param>
    /// <param name="retentionDays">
    /// New retention horizon in days (must be &gt; 0), or <c>null</c> to leave unchanged.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<TenantActivitySettingsSnapshot> SetAsync(
        Guid tenantId,
        ActivityCaptureLevel? level,
        int? retentionDays,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Immutable snapshot of one tenant's activity-capture settings.
/// </summary>
/// <param name="TenantId">The tenant the snapshot belongs to.</param>
/// <param name="Level">The active capture level.</param>
/// <param name="RetentionDays">
/// Number of days persisted rows are kept before the retention job
/// purges them.
/// </param>
public sealed record TenantActivitySettingsSnapshot(
    Guid TenantId,
    ActivityCaptureLevel Level,
    int RetentionDays);
