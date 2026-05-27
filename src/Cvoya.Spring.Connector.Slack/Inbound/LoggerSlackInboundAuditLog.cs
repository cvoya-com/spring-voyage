// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Connector.Slack.Inbound;

using Microsoft.Extensions.Logging;

/// <summary>
/// OSS-default <see cref="ISlackInboundAuditLog"/>. Writes a single
/// structured log line per inbound event so operators see the audit
/// trail via the platform's existing log pipeline. Cloud overlays
/// replace this with a richer surface that emits an
/// <see cref="Cvoya.Spring.Core.Capabilities.ActivityEvent"/>.
/// </summary>
public sealed class LoggerSlackInboundAuditLog : ISlackInboundAuditLog
{
    private readonly ILogger<LoggerSlackInboundAuditLog> _logger;

    /// <summary>Creates a new <see cref="LoggerSlackInboundAuditLog"/>.</summary>
    public LoggerSlackInboundAuditLog(ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _logger = loggerFactory.CreateLogger<LoggerSlackInboundAuditLog>();
    }

    /// <inheritdoc />
    public Task RecordAsync(SlackInboundAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        _logger.LogInformation(
            "Slack inbound audit: event_type={EventType} team_id={TeamId} slack_user_id={SlackUserId} acting_tenant_user_id={ActingTenantUserId} disposition={Disposition} detail={Detail}",
            auditEvent.EventType,
            auditEvent.TeamId,
            auditEvent.SlackUserId,
            auditEvent.ActingTenantUserId,
            auditEvent.Disposition,
            auditEvent.Detail);

        return Task.CompletedTask;
    }
}
