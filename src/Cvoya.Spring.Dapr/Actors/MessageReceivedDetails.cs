// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Builds the <c>Details</c> JSON payload attached to <c>MessageReceived</c>
/// activity events so the conversation surfaces (CLI <c>spring message show</c>,
/// <c>spring conversation show</c> / <c>inbox show</c>, plus the portal
/// thread view) can render the message body inline rather than only the
/// envelope summary line. Keeping the shape in a single helper means every
/// actor (<see cref="AgentActor"/>, <see cref="HumanActor"/>,
/// <see cref="UnitActor"/>) emits the same field set so downstream readers
/// can treat the payload as a stable schema (#1209).
/// </summary>
public static class MessageReceivedDetails
{
    /// <summary>JSON property name for the message id (a stringified <see cref="Guid"/>).</summary>
    public const string MessageIdProperty = "messageId";

    /// <summary>JSON property name for the message sender's <c>scheme://path</c>.</summary>
    public const string FromProperty = "from";

    /// <summary>JSON property name for the message recipient's <c>scheme://path</c>.</summary>
    public const string ToProperty = "to";

    /// <summary>JSON property name for the message type (<see cref="MessageType"/>).</summary>
    public const string MessageTypeProperty = "messageType";

    /// <summary>JSON property name for the rendered text body, when extractable.</summary>
    public const string BodyProperty = "body";

    /// <summary>JSON property name for the raw payload <see cref="JsonElement"/>.</summary>
    public const string PayloadProperty = "payload";

    /// <summary>
    /// Builds a <see cref="JsonElement"/> describing <paramref name="message"/>
    /// for persistence in <c>ActivityEvent.Details</c>. The returned element
    /// always carries <c>messageId</c>, <c>from</c>, <c>to</c>, and
    /// <c>messageType</c>; <c>body</c> is populated when the payload is a
    /// JSON string (the common case — <c>spring message send</c> wraps the
    /// caller's text as <c>UntypedString</c>); the structured payload always
    /// rides along under <c>payload</c> so non-text replies are still
    /// inspectable.
    /// </summary>
    /// <param name="message">The received message.</param>
    public static JsonElement Build(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        using var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString(MessageIdProperty, message.Id);
            writer.WriteString(FromProperty, FormatAddress(message.From));
            writer.WriteString(ToProperty, FormatAddress(message.To));
            writer.WriteString(MessageTypeProperty, message.Type.ToString());

            var body = TryExtractText(message.Payload);
            if (body is not null)
            {
                writer.WriteString(BodyProperty, body);
            }

            // The payload may be `default(JsonElement)` for control messages
            // like `Cancel` — guard the write so we don't emit `null` keys
            // for properties the caller didn't populate.
            if (message.Payload.ValueKind != JsonValueKind.Undefined)
            {
                writer.WritePropertyName(PayloadProperty);
                message.Payload.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        buffer.Position = 0;
        using var doc = JsonDocument.Parse(buffer);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Returns the rendered text from <paramref name="payload"/>.
    /// Recognises two shapes:
    /// <list type="bullet">
    ///   <item><description>
    ///     A bare JSON string — the <c>spring message send</c> /
    ///     <c>ThreadMessageRequest</c> path. The string is returned verbatim.
    ///   </description></item>
    ///   <item><description>
    ///     A JSON object with an <c>Output</c> string property — the shape
    ///     produced by <c>A2AExecutionDispatcher</c> for agent replies
    ///     (<c>{ Output, ExitCode, [Error] }</c>). The <c>Output</c> string is
    ///     returned so the thread surfaces render the agent's natural-language
    ///     reply rather than only the envelope summary line (#1547, #1549).
    ///   </description></item>
    /// </list>
    /// Returns <c>null</c> for any other shape (structured non-reply payloads,
    /// arrays, absent payloads); callers fall back to <c>payload</c> for those.
    /// </summary>
    public static string? TryExtractText(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.String)
        {
            return payload.GetString();
        }

        if (payload.ValueKind == JsonValueKind.Object
            && payload.TryGetProperty("Output", out var outputProp)
            && outputProp.ValueKind == JsonValueKind.String)
        {
            return outputProp.GetString();
        }

        return null;
    }

    /// <summary>
    /// Maximum summary length when truncating extracted message text for the
    /// <see cref="Capabilities.ActivityEvent.Summary"/> one-liner. The surfaces
    /// already render the full body from <see cref="BodyProperty"/>; the
    /// summary is just a glance-line.
    /// </summary>
    private const int SummaryMaxLength = 160;

    /// <summary>
    /// Builds the human-readable one-liner used for an
    /// <see cref="Capabilities.ActivityEventType.MessageReceived"/> activity
    /// event's <see cref="Capabilities.ActivityEvent.Summary"/> (#1636).
    /// <para>
    /// Production must NEVER emit the legacy
    /// <c>"Received {Type} message {Id} from {From}"</c> envelope template —
    /// it leaks raw GUIDs into every downstream surface (CLI, portal, inbox)
    /// and forces consumers to reverse-engineer the platform's summary to
    /// recover usable text. Instead, the summary line carries the actual
    /// message text (truncated for one-line display) when extractable, and a
    /// short non-leaky placeholder otherwise. The full body always rides
    /// alongside on <see cref="BodyProperty"/> so the portal renders chat
    /// bubbles directly without templating.
    /// </para>
    /// </summary>
    /// <param name="message">The received message.</param>
    /// <returns>A non-empty, GUID-free, address-free summary line.</returns>
    public static string BuildSummary(Message message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var body = TryExtractText(message.Payload);
        if (!string.IsNullOrWhiteSpace(body))
        {
            return Truncate(body!.Trim(), SummaryMaxLength);
        }

        // Control / structured-payload messages (Cancel, HealthCheck,
        // StatusQuery, ack envelopes, etc.) have no reader-visible text. A
        // short type label is safe — it carries no GUIDs and no addresses.
        return message.Type switch
        {
            MessageType.Domain => "Message received",
            MessageType.HealthCheck => "Health check received",
            MessageType.StatusQuery => "Status query received",
            MessageType.Cancel => "Cancel received",
            MessageType.PolicyUpdate => "Policy update received",
            MessageType.Amendment => "Amendment received",
            _ => $"{message.Type} received",
        };
    }

    private static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
        {
            return value;
        }

        // Leave room for the ellipsis character so the rendered glyph count
        // stays at maxLength — important for surfaces that hard-clip lines.
        return string.Concat(value.AsSpan(0, maxLength - 1), "…");
    }

    private static string FormatAddress(Address address) =>
        $"{address.Scheme}://{address.Path}";
}