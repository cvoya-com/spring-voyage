// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Observability;

using System.Text.Json;

/// <summary>
/// Detail view for a single message — the body, envelope, and the
/// conversation it belongs to. Backs <c>GET /api/v1/messages/{id}</c>
/// (#1209) and the CLI's <c>spring message show &lt;id&gt;</c>. Sourced from
/// the activity-event projection: every <c>MessageReceived</c> event the
/// platform emits stamps the envelope on its <c>Details</c> JSON, so a
/// dedicated message store is not yet required (mirrors the conversation
/// projection in <see cref="IConversationQueryService"/>).
/// </summary>
/// <param name="MessageId">The message identifier.</param>
/// <param name="ConversationId">The conversation the message was threaded into, when present.</param>
/// <param name="From">The sender address (<c>scheme://path</c>).</param>
/// <param name="To">The recipient address (<c>scheme://path</c>).</param>
/// <param name="MessageType">The message type (<c>Domain</c>, <c>StatusQuery</c>, ...).</param>
/// <param name="Body">The rendered text body when the payload is a JSON string; <c>null</c> for structured payloads.</param>
/// <param name="Payload">The raw payload as observed by the receiving actor.</param>
/// <param name="Timestamp">When the receiving actor logged the message.</param>
public record MessageDetail(
    Guid MessageId,
    string? ConversationId,
    string From,
    string To,
    string MessageType,
    string? Body,
    JsonElement? Payload,
    DateTimeOffset Timestamp);