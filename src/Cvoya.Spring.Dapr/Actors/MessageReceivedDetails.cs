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
    /// Returns the rendered text from <paramref name="payload"/> when the
    /// caller wrapped a string (the <c>spring message send</c> path), or
    /// <c>null</c> when the payload is structured (object/array) or absent.
    /// Callers fall back to <c>payload</c> for non-string bodies.
    /// </summary>
    public static string? TryExtractText(JsonElement payload)
    {
        return payload.ValueKind == JsonValueKind.String
            ? payload.GetString()
            : null;
    }

    private static string FormatAddress(Address address) =>
        $"{address.Scheme}://{address.Path}";
}