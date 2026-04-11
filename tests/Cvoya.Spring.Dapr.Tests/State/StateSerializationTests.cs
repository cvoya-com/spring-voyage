// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.State;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;

using FluentAssertions;

using Xunit;

/// <summary>
/// Validates that key state types survive System.Text.Json serialization roundtrips,
/// ensuring compatibility with Dapr's state store serialization.
/// </summary>
public class StateSerializationTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = null // PascalCase for internal Dapr state
    };

    [Fact]
    public void ConversationChannel_RoundTrip_PreservesAllProperties()
    {
        var original = new ConversationChannel
        {
            ConversationId = "conv-123",
            Messages =
            [
                new Message(
                    Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                    new Address("agent", "sender-1"),
                    new Address("agent", "receiver-1"),
                    MessageType.Domain,
                    "conv-123",
                    JsonSerializer.SerializeToElement(new { Task = "implement feature" }),
                    DateTimeOffset.Parse("2026-01-15T10:30:00+00:00"))
            ],
            CreatedAt = DateTimeOffset.Parse("2026-01-15T10:00:00+00:00")
        };

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<ConversationChannel>(json, Options);

        deserialized.Should().NotBeNull();
        deserialized!.ConversationId.Should().Be(original.ConversationId);
        deserialized.Messages.Should().HaveCount(1);
        deserialized.CreatedAt.Should().Be(original.CreatedAt);

        var message = deserialized.Messages[0];
        message.Id.Should().Be(original.Messages[0].Id);
        message.From.Should().Be(original.Messages[0].From);
        message.To.Should().Be(original.Messages[0].To);
        message.Type.Should().Be(MessageType.Domain);
        message.ConversationId.Should().Be("conv-123");
    }

    [Fact]
    public void Message_RoundTrip_PreservesAllProperties()
    {
        var original = new Message(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            new Address("unit", "engineering/backend"),
            new Address("agent", "ada"),
            MessageType.StatusQuery,
            "conv-456",
            JsonSerializer.SerializeToElement(new { Status = "Active", Items = new[] { 1, 2, 3 } }),
            DateTimeOffset.Parse("2026-03-20T14:00:00+00:00"));

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<Message>(json, Options);

        deserialized.Should().NotBeNull();
        deserialized!.Id.Should().Be(original.Id);
        deserialized.From.Should().Be(original.From);
        deserialized.To.Should().Be(original.To);
        deserialized.Type.Should().Be(MessageType.StatusQuery);
        deserialized.ConversationId.Should().Be("conv-456");
        deserialized.Timestamp.Should().Be(original.Timestamp);
        deserialized.Payload.GetProperty("Status").GetString().Should().Be("Active");
    }

    [Fact]
    public void Address_RoundTrip_PreservesSchemeAndPath()
    {
        var original = new Address("connector", "github/spring-voyage");

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<Address>(json, Options);

        deserialized.Should().NotBeNull();
        deserialized!.Scheme.Should().Be("connector");
        deserialized.Path.Should().Be("github/spring-voyage");
    }

    [Fact]
    public void Message_WithNullConversationId_RoundTrips()
    {
        var original = new Message(
            Guid.NewGuid(),
            new Address("agent", "sender"),
            new Address("agent", "receiver"),
            MessageType.HealthCheck,
            null,
            JsonSerializer.SerializeToElement(new { Healthy = true }),
            DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<Message>(json, Options);

        deserialized.Should().NotBeNull();
        deserialized!.ConversationId.Should().BeNull();
    }

    [Fact]
    public void ConversationChannel_EmptyMessages_RoundTrips()
    {
        var original = new ConversationChannel
        {
            ConversationId = "empty-conv",
            Messages = [],
            CreatedAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<ConversationChannel>(json, Options);

        deserialized.Should().NotBeNull();
        deserialized!.ConversationId.Should().Be("empty-conv");
        deserialized.Messages.Should().BeEmpty();
    }
}