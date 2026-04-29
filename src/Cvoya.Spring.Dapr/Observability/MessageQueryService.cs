// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Default <see cref="IMessageQueryService"/>. Looks up the most recent
/// <c>MessageReceived</c> activity event whose <c>Details</c> JSON carries
/// the requested <c>messageId</c> (the envelope written by
/// <see cref="MessageReceivedDetails.Build"/>) and projects it into a
/// <see cref="MessageDetail"/> so the CLI / portal can render the body.
/// </summary>
/// <remarks>
/// We scan every recorded MessageReceived event because the activity table
/// stores <c>Details</c> as opaque JSON in the OSS surface — a JSON-aware
/// implementation can be added later. The platform emits a single
/// <c>MessageReceived</c> per recipient per message, so the result set is
/// small in practice; cloud overlays with a dedicated message table can
/// swap the implementation through DI without touching callers.
/// </remarks>
public class MessageQueryService(SpringDbContext dbContext) : IMessageQueryService
{
    private static readonly string MessageReceivedName = nameof(ActivityEventType.MessageReceived);

    /// <inheritdoc />
    public async Task<MessageDetail?> GetAsync(Guid messageId, CancellationToken cancellationToken)
    {
        if (messageId == Guid.Empty)
        {
            return null;
        }

        // Pull every MessageReceived event with a non-null Details column —
        // the JSON-side filter happens client-side because EF's JSON path
        // operators are provider-specific (Postgres vs. in-memory tests).
        var rows = await dbContext.ActivityEvents
            .Where(e => e.EventType == MessageReceivedName && e.Details != null)
            .OrderByDescending(e => e.Timestamp)
            .Select(e => new
            {
                e.Id,
                e.Timestamp,
                e.CorrelationId,
                e.Details,
            })
            .ToListAsync(cancellationToken);

        var idText = messageId.ToString();
        foreach (var row in rows)
        {
            if (row.Details is not JsonElement details || details.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            if (!details.TryGetProperty(MessageReceivedDetails.MessageIdProperty, out var idProp))
            {
                continue;
            }
            if (idProp.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            if (!string.Equals(idProp.GetString(), idText, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var from = TryReadString(details, MessageReceivedDetails.FromProperty) ?? string.Empty;
            var to = TryReadString(details, MessageReceivedDetails.ToProperty) ?? string.Empty;
            var messageType = TryReadString(details, MessageReceivedDetails.MessageTypeProperty) ?? string.Empty;
            var body = TryReadString(details, MessageReceivedDetails.BodyProperty);

            JsonElement? payload = null;
            if (details.TryGetProperty(MessageReceivedDetails.PayloadProperty, out var payloadProp)
                && payloadProp.ValueKind != JsonValueKind.Undefined
                && payloadProp.ValueKind != JsonValueKind.Null)
            {
                payload = payloadProp.Clone();
            }

            return new MessageDetail(
                MessageId: messageId,
                ThreadId: row.CorrelationId,
                From: from,
                To: to,
                MessageType: messageType,
                Body: body,
                Payload: payload,
                Timestamp: row.Timestamp);
        }

        return null;
    }

    private static string? TryReadString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var prop))
        {
            return null;
        }
        return prop.ValueKind == JsonValueKind.String ? prop.GetString() : null;
    }
}