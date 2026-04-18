// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Actors;

using System.Text.Json;
using System.Text.Json.Serialization;

using Cvoya.Spring.Core.Messaging;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> used by both ends of Dapr's
/// actor-remoting pipe to round-trip a <see cref="Message"/> — including its
/// <see cref="Message.Payload"/> <see cref="JsonElement"/> — losslessly.
/// </summary>
/// <remarks>
/// <para>
/// Dapr's actor remoting defaults to <c>DataContractSerializer</c>. That
/// serializer has no knowledge of <see cref="JsonElement"/>, so a
/// <see cref="Message.Payload"/> produced on the actor side via
/// <c>JsonSerializer.SerializeToElement(...)</c> round-trips back to the
/// caller as <c>default(JsonElement)</c> — i.e. an uninitialized struct
/// whose <c>_parent</c> is <c>null</c>. Any subsequent read of that value
/// (including the re-serialization ASP.NET Core performs when writing the
/// HTTP response) throws
/// <see cref="System.InvalidOperationException"/>: "Operation is not valid
/// due to the current state of the object", which surfaces in production as
/// an HTTP 500 from <c>GET /api/v1/agents/{id}</c> and other endpoints that
/// return <see cref="Message.Payload"/>.
/// </para>
/// <para>
/// Switching actor-remoting to JSON serialization closes that seam. The
/// custom <see cref="JsonElementConverter"/> below is the single place we
/// control where raw wire JSON is parsed into a <see cref="JsonElement"/>;
/// it parses into a transient <see cref="JsonDocument"/> and returns a
/// <see cref="JsonElement.Clone"/> so the produced element is detached
/// from any document owned by the (short-lived) deserialization scope. Every
/// endpoint that hands a <see cref="Message.Payload"/> back to ASP.NET —
/// status query, conversation reply, domain response, etc. — inherits the
/// fix without per-call-site clones.
/// </para>
/// <para>
/// Both the <see cref="global::Dapr.Actors.Client.ActorProxyOptions"/> on
/// the caller side and the
/// <see cref="global::Dapr.Actors.Runtime.ActorRuntimeOptions"/> on the
/// actor-host side must be configured with these options so the two sides
/// agree on JSON as the wire format.
/// </para>
/// </remarks>
public static class ActorRemotingJsonOptions
{
    /// <summary>
    /// The shared options instance. Safe for cross-thread reuse because
    /// <see cref="JsonSerializerOptions"/> is immutable once it has been
    /// used for (de)serialization.
    /// </summary>
    public static JsonSerializerOptions Instance { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new DetachedJsonElementConverter());
        return options;
    }

    /// <summary>
    /// Replaces the built-in <see cref="JsonElement"/> converter with one
    /// that (a) writes <c>default(JsonElement)</c> as a JSON null — the
    /// built-in converter throws <see cref="InvalidOperationException"/>
    /// here, and <c>Message.Payload</c> is legitimately <c>default</c> for
    /// empty requests like StatusQuery; and (b) on read, parses into a
    /// dedicated <see cref="JsonDocument"/> and returns
    /// <see cref="JsonElement.Clone"/>, producing a self-contained element
    /// whose lifetime is decoupled from the deserialization call frame.
    /// </summary>
    private sealed class DetachedJsonElementConverter : JsonConverter<JsonElement>
    {
        public override JsonElement Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                // Mirror Write's treatment of default(JsonElement): a JSON
                // null maps back to the default struct value so the
                // Request/Response round-trip of a no-payload Message is
                // idempotent.
                return default;
            }

            using var document = JsonDocument.ParseValue(ref reader);
            return document.RootElement.Clone();
        }

        public override void Write(Utf8JsonWriter writer, JsonElement value, JsonSerializerOptions options)
        {
            // ValueKind == Undefined indicates a default(JsonElement) whose
            // _parent is null; value.WriteTo(writer) would throw
            // "Operation is not valid due to the current state of the
            // object" for that case. Emit a JSON null instead so callers
            // (e.g. MessageEndpoints building a StatusQuery with
            // `default` as Payload) don't have to manufacture an empty
            // JsonElement just to satisfy the serializer.
            if (value.ValueKind == JsonValueKind.Undefined)
            {
                writer.WriteNullValue();
                return;
            }

            value.WriteTo(writer);
        }
    }
}