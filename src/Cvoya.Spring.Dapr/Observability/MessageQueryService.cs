// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Observability;

using System.Text.Json;

using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Dapr.Data;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// Default <see cref="IMessageQueryService"/>. Reads from the EF-authoritative
/// <c>messages</c> table landed by ADR-0030 / ADR-0040; the legacy
/// <c>activity_events.Details</c> JSON-scan path is gone.
/// </summary>
/// <remarks>
/// Single indexed lookup on the primary key (<c>messages.id</c>); tenant
/// scoping flows from the <see cref="SpringDbContext"/> query filter on
/// <see cref="Cvoya.Spring.Dapr.Data.Entities.MessageEntity"/>. The wire shape
/// (<see cref="MessageDetail"/>) is unchanged, so callers do not move when
/// the read source does.
/// </remarks>
public class MessageQueryService(SpringDbContext dbContext) : IMessageQueryService
{
    /// <inheritdoc />
    public async Task<MessageDetail?> GetAsync(Guid messageId, CancellationToken cancellationToken)
    {
        if (messageId == Guid.Empty)
        {
            return null;
        }

        var row = await dbContext.Messages
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken);

        if (row is null)
        {
            return null;
        }

        var payload = TryParsePayload(row.Payload);

        return new MessageDetail(
            MessageId: row.Id,
            ThreadId: GuidFormatter.Format(row.ThreadId),
            From: $"{row.SenderScheme}://{GuidFormatter.Format(row.SenderId)}",
            To: $"{row.RecipientScheme}://{GuidFormatter.Format(row.RecipientId)}",
            MessageType: row.MessageType,
            Body: row.Body,
            Payload: payload,
            Timestamp: row.SentAt,
            InReplyTo: row.InReplyTo);
    }

    /// <summary>
    /// Parses the persisted JSON payload back into a <see cref="JsonElement"/>.
    /// Stored values are always valid JSON (the writer serialises with
    /// <see cref="JsonSerializer"/> and writes <c>"null"</c> for undefined
    /// payloads), but a defensive try/catch keeps the read path resilient if
    /// a future writer or hand-edit produces something off-shape.
    /// </summary>
    private static JsonElement? TryParsePayload(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
