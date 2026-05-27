// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Inbound;

/// <summary>
/// Captures the audit trail for inbound Slack events per ADR-0062 §4
/// — every inbound event records <c>acting_tenant_user_id</c> so the
/// activity envelope keys correctly. The OSS surface is a logger-
/// backed writer; cloud overlays can substitute a structured-store
/// implementation that surfaces in the activity-event timeline.
///
/// <para>
/// The interface is connector-internal: the platform's
/// <see cref="Cvoya.Spring.Core.Capabilities.IActivityEventBus"/> is
/// the right home for production-quality activity capture, but the
/// inbound path needs a contract that can be substituted in unit
/// tests without booting the full event-bus + persistence graph.
/// </para>
/// </summary>
public interface ISlackInboundAuditLog
{
    /// <summary>
    /// Records the disposition of an inbound Slack event.
    /// </summary>
    /// <param name="auditEvent">
    /// The structured audit entry. The <see cref="SlackInboundAuditEvent.ActingTenantUserId"/>
    /// is the routable identity on whose behalf the event landed — in
    /// OSS always <c>OssTenantUserIds.Operator</c> per ADR-0061 §2.1.
    /// </param>
    /// <param name="cancellationToken">Propagates cancellation.</param>
    Task RecordAsync(SlackInboundAuditEvent auditEvent, CancellationToken cancellationToken = default);
}

/// <summary>
/// One inbound Slack event in the audit trail.
/// </summary>
/// <param name="EventType">
/// Slack's <c>event.type</c> (e.g. <c>"message.im"</c>,
/// <c>"member_joined_channel"</c>) or
/// <c>"command:&lt;name&gt;"</c> for slash commands.
/// </param>
/// <param name="TeamId">Slack workspace id.</param>
/// <param name="SlackUserId">The Slack user the event originated from.</param>
/// <param name="ActingTenantUserId">
/// The mapped <c>TenantUser</c>, or <c>null</c> when the originating
/// user is unbound. ADR-0062 §4 requires the audit envelope to carry
/// this when known.
/// </param>
/// <param name="Disposition">
/// Free-form short string describing what the connector did
/// (<c>"dropped:no-thread"</c>, <c>"refused:unbound"</c>,
/// <c>"auto-leave"</c>, <c>"forwarded"</c>, ...).
/// </param>
/// <param name="Detail">
/// Optional longer-form context for the audit consumer.
/// </param>
public sealed record SlackInboundAuditEvent(
    string EventType,
    string TeamId,
    string SlackUserId,
    Guid? ActingTenantUserId,
    string Disposition,
    string? Detail = null);
