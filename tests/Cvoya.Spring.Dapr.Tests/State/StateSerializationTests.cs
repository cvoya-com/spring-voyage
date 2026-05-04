// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.State;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;

using Shouldly;

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
    public void ThreadChannel_RoundTrip_PreservesAllProperties()
    {
        var original = new ThreadChannel
        {
            ThreadId = "conv-123",
            Messages =
            [
                new Message(
                    Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
                    Address.For("agent", TestSlugIds.HexFor("sender-1")),
                    Address.For("agent", TestSlugIds.HexFor("receiver-1")),
                    MessageType.Domain,
                    "conv-123",
                    JsonSerializer.SerializeToElement(new { Task = "implement feature" }),
                    DateTimeOffset.Parse("2026-01-15T10:30:00+00:00"))
            ],
            CreatedAt = DateTimeOffset.Parse("2026-01-15T10:00:00+00:00")
        };

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<ThreadChannel>(json, Options);

        deserialized.ShouldNotBeNull();
        deserialized!.ThreadId.ShouldBe(original.ThreadId);
        deserialized.Messages.Count().ShouldBe(1);
        deserialized.CreatedAt.ShouldBe(original.CreatedAt);

        var message = deserialized.Messages[0];
        message.Id.ShouldBe(original.Messages[0].Id);
        message.From.ShouldBe(original.Messages[0].From);
        message.To.ShouldBe(original.Messages[0].To);
        message.Type.ShouldBe(MessageType.Domain);
        message.ThreadId.ShouldBe("conv-123");
    }

    [Fact]
    public void Message_RoundTrip_PreservesAllProperties()
    {
        var original = new Message(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Address.For("unit", TestSlugIds.HexFor("engineering/backend")),
            Address.For("agent", TestSlugIds.HexFor("ada")),
            MessageType.StatusQuery,
            "conv-456",
            JsonSerializer.SerializeToElement(new { Status = "Active", Items = new[] { 1, 2, 3 } }),
            DateTimeOffset.Parse("2026-03-20T14:00:00+00:00"));

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<Message>(json, Options);

        deserialized.ShouldNotBeNull();
        deserialized!.Id.ShouldBe(original.Id);
        deserialized.From.ShouldBe(original.From);
        deserialized.To.ShouldBe(original.To);
        deserialized.Type.ShouldBe(MessageType.StatusQuery);
        deserialized.ThreadId.ShouldBe("conv-456");
        deserialized.Timestamp.ShouldBe(original.Timestamp);
        deserialized.Payload.GetProperty("Status").GetString().ShouldBe("Active");
    }

    [Fact]
    public void Address_RoundTrip_PreservesSchemeAndPath()
    {
        var original = Address.For("connector", TestSlugIds.HexFor("github/spring-voyage"));

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<Address>(json, Options);

        deserialized.ShouldNotBeNull();
        deserialized!.Scheme.ShouldBe("connector");
        deserialized.Path.ShouldBe(TestSlugIds.HexFor("github/spring-voyage"));
    }

    [Fact]
    public void Message_WithNullThreadId_RoundTrips()
    {
        var original = new Message(
            Guid.NewGuid(),
            Address.For("agent", TestSlugIds.HexFor("sender")),
            Address.For("agent", TestSlugIds.HexFor("receiver")),
            MessageType.HealthCheck,
            null,
            JsonSerializer.SerializeToElement(new { Healthy = true }),
            DateTimeOffset.UtcNow);

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<Message>(json, Options);

        deserialized.ShouldNotBeNull();
        deserialized!.ThreadId.ShouldBeNull();
    }

    [Fact]
    public void ThreadChannel_EmptyMessages_RoundTrips()
    {
        var original = new ThreadChannel
        {
            ThreadId = "empty-conv",
            Messages = [],
            CreatedAt = DateTimeOffset.UtcNow
        };

        var json = JsonSerializer.Serialize(original, Options);
        var deserialized = JsonSerializer.Deserialize<ThreadChannel>(json, Options);

        deserialized.ShouldNotBeNull();
        deserialized!.ThreadId.ShouldBe("empty-conv");
        deserialized.Messages.ShouldBeEmpty();
    }
}