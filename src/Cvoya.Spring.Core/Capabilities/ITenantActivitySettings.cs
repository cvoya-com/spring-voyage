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
    /// Any field can be left as the current value by passing
    /// <c>null</c>; passing every value as <c>null</c> is a no-op.
    /// </summary>
    /// <param name="tenantId">The tenant whose settings change.</param>
    /// <param name="level">New capture level, or <c>null</c> to leave unchanged.</param>
    /// <param name="retentionDays">
    /// New retention horizon in days (must be &gt; 0), or <c>null</c> to leave unchanged.
    /// </param>
    /// <param name="externalForward">
    /// New external-forwarding configuration (#2503), or <c>null</c> to
    /// leave unchanged. Pass <see cref="ExternalForwardUpdate.Clear"/>
    /// to remove an existing block (forwarding off + endpoint cleared).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task<TenantActivitySettingsSnapshot> SetAsync(
        Guid tenantId,
        ActivityCaptureLevel? level,
        int? retentionDays,
        ExternalForwardUpdate? externalForward = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Tri-state for the external-forward setter (#2503): leave the
/// current value, set a new one, or clear the existing config.
/// </summary>
public sealed record ExternalForwardUpdate
{
    /// <summary>
    /// Replace the persisted forward config with <see cref="Config"/>.
    /// </summary>
    public ExternalOtelForwardConfig? Config { get; init; }

    /// <summary>
    /// When true, clears the persisted block irrespective of <see cref="Config"/>.
    /// </summary>
    public bool ClearExisting { get; init; }

    /// <summary>Sentinel meaning "set this config".</summary>
    public static ExternalForwardUpdate Set(ExternalOtelForwardConfig config)
        => new() { Config = config };

    /// <summary>Sentinel meaning "drop the persisted block".</summary>
    public static ExternalForwardUpdate Clear { get; } = new() { ClearExisting = true };
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
/// <param name="ExternalForward">
/// Optional external OTel-backend forwarding configuration (issue #2503).
/// Null when forwarding is disabled. When set and
/// <see cref="ExternalOtelForwardConfig.Enabled"/> is <c>true</c>, the
/// activity-capture pipeline forwards a redacted copy of every accepted
/// event to <see cref="ExternalOtelForwardConfig.Endpoint"/>
/// best-effort.
/// </param>
public sealed record TenantActivitySettingsSnapshot(
    Guid TenantId,
    ActivityCaptureLevel Level,
    int RetentionDays,
    ExternalOtelForwardConfig? ExternalForward = null);

/// <summary>
/// Per-tenant external OpenTelemetry forwarding configuration (#2503).
/// Wired onto <see cref="TenantActivitySettingsSnapshot.ExternalForward"/>
/// and consumed by the activity-capture forwarder decorator.
/// </summary>
/// <param name="Endpoint">OTLP/HTTP collector base URL (e.g. <c>https://otel.datadog.com</c>).</param>
/// <param name="Protocol">
/// Wire protocol: <c>http/json</c> | <c>http/protobuf</c>. Defaults to
/// <c>http/protobuf</c> when unset (matches the launcher default).
/// </param>
/// <param name="Headers">
/// Opaque header map sent on every outbound request (Datadog API key,
/// Tempo tenant id, etc.). Treated as secret material — never echoed
/// back to API surfaces.
/// </param>
/// <param name="Enabled">
/// Master switch. <c>false</c> keeps the row but skips forwarding,
/// so operators can pause without losing the endpoint config.
/// </param>
public sealed record ExternalOtelForwardConfig(
    string Endpoint,
    string Protocol,
    IReadOnlyDictionary<string, string> Headers,
    bool Enabled);
