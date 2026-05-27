// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Endpoints;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Shared helper that emits the API-side outbound
/// <see cref="ActivityEventType.MessageSent"/> envelope ADR-0062 § 4
/// requires. The envelope dual-stamps the routable <c>human://</c>
/// <c>from.address</c> (mirrors <see cref="Message.From"/>) and the
/// authenticating <c>tenant-user://</c> principal
/// (<c>acting_tenant_user_id</c>) so the permission decision is
/// reconstructible from observation alone — the OSS render strips the
/// acting principal (it always equals the operator) while the cloud
/// render keeps it.
/// </summary>
public static class OutboundMessageAuditEmitter
{
    /// <summary>JSON property name on the activity envelope: routable sender address.</summary>
    public const string FromAddressProperty = "from";

    /// <summary>JSON property name on the activity envelope: routable recipient address.</summary>
    public const string ToAddressProperty = "to";

    /// <summary>
    /// JSON property name on the activity envelope: the auth principal that
    /// drove the API call (ADR-0062 § 4). The cloud render keeps this in
    /// the default view; the OSS render strips it.
    /// </summary>
    public const string ActingTenantUserIdProperty = "acting_tenant_user_id";

    /// <summary>
    /// Publishes the activity envelope on <paramref name="activityEventBus"/>.
    /// Best-effort — a publish failure never propagates; the send must
    /// succeed even if observability is degraded.
    /// </summary>
    /// <param name="activityEventBus">The bus the envelope is published on.</param>
    /// <param name="message">The outbound message that was just constructed.</param>
    /// <param name="caller">
    /// The authenticated caller principal (the auth identity), distinct
    /// from <see cref="Message.From"/> (the speaking-as Hat).
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    public static async Task EmitAsync(
        IActivityEventBus activityEventBus,
        Message message,
        Address caller,
        CancellationToken cancellationToken)
    {
        Guid? correlation = null;
        if (!string.IsNullOrWhiteSpace(message.ThreadId)
            && GuidFormatter.TryParse(message.ThreadId, out var parsedThreadId))
        {
            correlation = parsedThreadId;
        }

        var details = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            [FromAddressProperty] = new
            {
                address = $"{message.From.Scheme}://{message.From.Path}",
                scheme = message.From.Scheme,
            },
            [ToAddressProperty] = new
            {
                address = $"{message.To.Scheme}://{message.To.Path}",
            },
            ["messageId"] = message.Id.ToString("D"),
            [ActingTenantUserIdProperty] = string.Equals(
                caller.Scheme,
                Address.TenantUserScheme,
                StringComparison.OrdinalIgnoreCase)
                ? $"{caller.Scheme}://{caller.Path}"
                : null,
        });

        var envelope = new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            message.From,
            ActivityEventType.MessageSent,
            ActivitySeverity.Info,
            $"Message sent to {message.To.Scheme}://{message.To.Path}",
            details,
            correlation?.ToString("D"));

        try
        {
            await activityEventBus.PublishAsync(envelope, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _ = ex;
        }
    }
}
