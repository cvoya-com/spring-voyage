// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using System.Runtime.Serialization;
using System.Text.Json;
using System.Xml;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;

using Shouldly;

using Xunit;

/// <summary>
/// Regression tests for the <see cref="ActorRemotingJsonOptions"/> seam that
/// backs Dapr actor-remoting serialization for <see cref="Message"/>.
///
/// <para>
/// Dapr's default <c>DataContractSerializer</c> cannot round-trip a
/// <see cref="JsonElement"/>, which is what <see cref="Message.Payload"/>
/// is. Every status query, conversation reply, and domain response that
/// flowed through the actor-remoting boundary came back to the API with
/// <c>Payload = default(JsonElement)</c>, and ASP.NET Core's response
/// writer then threw
/// <c>InvalidOperationException: "Operation is not valid due to the current
/// state of the object"</c> inside <c>JsonElementConverter.Write</c> —
/// manifesting in production as an HTTP 500 from
/// <c>GET /api/v1/agents/{id}</c> (plus every other endpoint that returns a
/// <c>Message.Payload</c>).
/// </para>
///
/// <para>
/// The fix switches actor remoting to JSON via
/// <see cref="ActorRemotingJsonOptions"/>, which also swaps in a custom
/// <c>JsonElement</c> converter that parses into a dedicated
/// <see cref="JsonDocument"/> and returns <see cref="JsonElement.Clone"/>
/// so the produced element is detached from the transient document owned
/// by the deserialization scope.
/// </para>
/// </summary>
public class ActorRemotingJsonOptionsTests
{
    [Fact]
    public void Options_round_trip_Message_with_JsonElement_payload_losslessly()
    {
        // Arrange: build a Message the way the actor does — payload is a
        // JsonElement produced by SerializeToElement, exactly as
        // AgentActor.HandleStatusQueryAsync would produce.
        var payload = JsonSerializer.SerializeToElement(new
        {
            Status = "Idle",
            ActiveThreadId = (string?)null,
            PendingConversationCount = 0,
        });

        var message = new Message(
            Guid.NewGuid(),
            new Address("agent", "@actor-id"),
            new Address("human", "api"),
            MessageType.StatusQuery,
            null,
            payload,
            DateTimeOffset.UtcNow);

        // Act: the actor-remoting boundary is a serialize / deserialize
        // pair using the shared options.
        var json = JsonSerializer.Serialize(message, ActorRemotingJsonOptions.Instance);
        var hydrated = JsonSerializer.Deserialize<Message>(json, ActorRemotingJsonOptions.Instance);

        // Assert: the hydrated payload must be a valid JsonElement whose
        // re-serialization matches the original. Re-serialization is
        // exactly what ASP.NET Core performs when the endpoint writes the
        // HTTP response body, so this is the production failure mode.
        hydrated.ShouldNotBeNull();
        hydrated.Payload.ValueKind.ShouldBe(JsonValueKind.Object);
        JsonSerializer.Serialize(hydrated.Payload).ShouldBe(JsonSerializer.Serialize(payload));
        hydrated.Payload.GetProperty("Status").GetString().ShouldBe("Idle");
        hydrated.Payload.GetProperty("PendingConversationCount").GetInt32().ShouldBe(0);
    }

    /// <summary>
    /// Proves the production failure mode: Dapr's default
    /// <see cref="DataContractSerializer"/> cannot round-trip a
    /// <see cref="JsonElement"/> inside <see cref="Message.Payload"/>. The
    /// deserialized value is <c>default(JsonElement)</c>, whose <c>_parent</c>
    /// field is <c>null</c>, and any subsequent access — including the
    /// re-serialization ASP.NET Core performs when writing the HTTP
    /// response — throws
    /// <see cref="InvalidOperationException"/>: "Operation is not valid
    /// due to the current state of the object". This is the exact stack
    /// trace observed in production for <c>GET /api/v1/agents/{id}</c>.
    /// </summary>
    [Fact]
    public void DataContract_round_trip_of_Message_produces_an_unusable_JsonElement_payload()
    {
        var payload = JsonSerializer.SerializeToElement(new { Status = "Idle" });
        var message = new Message(
            Guid.NewGuid(),
            new Address("agent", "@actor-id"),
            new Address("human", "api"),
            MessageType.StatusQuery,
            null,
            payload,
            DateTimeOffset.UtcNow);

        // Arrange: the exact serialization pipeline Dapr uses when
        // UseJsonSerialization is false (its default).
        var serializer = new DataContractSerializer(typeof(Message));
        var stream = new MemoryStream();
        var writer = XmlDictionaryWriter.CreateBinaryWriter(stream, dictionary: null, session: null, ownsStream: false);
        serializer.WriteObject(writer, message);
        writer.Flush();

        stream.Position = 0;
        using var reader = XmlDictionaryReader.CreateBinaryReader(stream, XmlDictionaryReaderQuotas.Max);
        var hydrated = (Message)serializer.ReadObject(reader)!;

        // Act + Assert: the hydrated payload is default(JsonElement).
        hydrated.Payload.ValueKind.ShouldBe(JsonValueKind.Undefined);

        // Any downstream read re-produces the production failure. This is
        // literally the call ASP.NET Core makes when writing the HTTP
        // response, via JsonElementConverter.Write → JsonElement.WriteTo →
        // CheckValidInstance → throw.
        Should.Throw<InvalidOperationException>(() =>
            JsonSerializer.Serialize(hydrated.Payload));
    }

    /// <summary>
    /// StatusQuery / HealthCheck request messages carry
    /// <c>default(JsonElement)</c> as their payload — the caller has no
    /// body to send. The built-in <see cref="JsonElement"/> converter
    /// throws on <c>default(JsonElement)</c>, so outbound actor-remoting
    /// requests failed at serialization time even with
    /// <c>UseJsonSerialization = true</c> turned on. The detached converter
    /// must emit JSON null for <c>default(JsonElement)</c> and read it
    /// back as <c>default(JsonElement)</c> so the Message contract is
    /// preserved end-to-end.
    /// </summary>
    [Fact]
    public void Options_round_trip_Message_with_default_JsonElement_payload()
    {
        var message = new Message(
            Guid.NewGuid(),
            new Address("human", "api"),
            new Address("agent", "@actor-id"),
            MessageType.StatusQuery,
            null,
            default, // no body, as produced by MessageEndpoints / AgentEndpoints StatusQuery builders.
            DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(message, ActorRemotingJsonOptions.Instance);
        json.ShouldContain("\"Payload\":null");

        var hydrated = JsonSerializer.Deserialize<Message>(json, ActorRemotingJsonOptions.Instance);
        hydrated.ShouldNotBeNull();
        hydrated.Payload.ValueKind.ShouldBe(JsonValueKind.Undefined);
    }

    [Fact]
    public void JsonElement_payload_survives_disposal_of_the_transient_parse_document()
    {
        // Arrange: serialize a Message via the shared options, then
        // deserialize through a MemoryStream to mirror the streaming
        // deserialization path Dapr uses for actor-remoting response
        // bodies (JsonSerializer.DeserializeAsync over the HTTP response
        // stream).
        var payload = JsonSerializer.SerializeToElement(new { Healthy = true, Tick = 42 });
        var original = new Message(
            Guid.NewGuid(),
            new Address("agent", "@actor-id"),
            new Address("agent", "@caller-id"),
            MessageType.HealthCheck,
            null,
            payload,
            DateTimeOffset.UtcNow);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(original, ActorRemotingJsonOptions.Instance);

        using var stream = new MemoryStream(bytes);

        // Act: deserialize with the shared options. The JsonElement
        // returned by the converter must be detached from the internal
        // JsonDocument the converter creates during the call — otherwise
        // the element is tied to a document that disposes when the `using`
        // in the converter ends, and any subsequent read throws the same
        // "Operation is not valid…" exception seen in production.
        var hydrated = JsonSerializer.Deserialize<Message>(stream, ActorRemotingJsonOptions.Instance);

        // Force a GC cycle to make transient parse buffers reachable only
        // via the returned JsonElement (if the converter failed to
        // detach, this is where asynchronous cleanup would make the next
        // access fail).
        GC.Collect();
        GC.WaitForPendingFinalizers();

        // Assert: the element must still be readable. Both
        // JsonSerializer.Serialize(JsonElement) and GetRawText() invoke
        // CheckValidInstance() — which is what throws on a
        // default(JsonElement) or a disposed document.
        hydrated.ShouldNotBeNull();
        JsonSerializer.Serialize(hydrated.Payload).ShouldContain("\"Healthy\"");
        hydrated.Payload.GetRawText().ShouldContain("\"Tick\":42");
    }
}