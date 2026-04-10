// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;

using FluentAssertions;

using Xunit;

public class AddressTests
{
    [Fact]
    public void Equals_SameSchemeAndPath_ReturnsTrue()
    {
        var address1 = new Address("agent", "engineering-team/ada");
        var address2 = new Address("agent", "engineering-team/ada");

        address1.Should().Be(address2);
    }

    [Fact]
    public void Equals_DifferentScheme_ReturnsFalse()
    {
        var address1 = new Address("agent", "engineering-team/ada");
        var address2 = new Address("unit", "engineering-team/ada");

        address1.Should().NotBe(address2);
    }

    [Fact]
    public void Equals_DifferentPath_ReturnsFalse()
    {
        var address1 = new Address("agent", "engineering-team/ada");
        var address2 = new Address("agent", "engineering-team/bob");

        address1.Should().NotBe(address2);
    }

    [Fact]
    public void GetHashCode_EqualAddresses_ReturnsSameHashCode()
    {
        var address1 = new Address("agent", "engineering-team/ada");
        var address2 = new Address("agent", "engineering-team/ada");

        address1.GetHashCode().Should().Be(address2.GetHashCode());
    }
}

public class MessageTests
{
    [Fact]
    public void Constructor_WithValidParameters_CreatesMessage()
    {
        var id = Guid.NewGuid();
        var from = new Address("agent", "sender");
        var to = new Address("agent", "receiver");
        var payload = JsonDocument.Parse("{\"key\":\"value\"}").RootElement;
        var timestamp = DateTimeOffset.UtcNow;

        var message = new Message(id, from, to, MessageType.Domain, "conv-1", payload, timestamp);

        message.Id.Should().Be(id);
        message.From.Should().Be(from);
        message.To.Should().Be(to);
        message.Type.Should().Be(MessageType.Domain);
        message.ConversationId.Should().Be("conv-1");
        message.Payload.GetProperty("key").GetString().Should().Be("value");
        message.Timestamp.Should().Be(timestamp);
    }

    [Fact]
    public void Constructor_WithNullConversationId_CreatesMessage()
    {
        var payload = JsonDocument.Parse("{}").RootElement;

        var message = new Message(
            Guid.NewGuid(),
            new Address("agent", "sender"),
            new Address("agent", "receiver"),
            MessageType.HealthCheck,
            null,
            payload,
            DateTimeOffset.UtcNow);

        message.ConversationId.Should().BeNull();
    }

    [Fact]
    public void Equals_SameValues_ReturnsTrue()
    {
        var id = Guid.NewGuid();
        var from = new Address("agent", "sender");
        var to = new Address("agent", "receiver");
        var payload = JsonDocument.Parse("{}").RootElement;
        var timestamp = DateTimeOffset.UtcNow;

        var message1 = new Message(id, from, to, MessageType.Domain, "conv-1", payload, timestamp);
        var message2 = new Message(id, from, to, MessageType.Domain, "conv-1", payload, timestamp);

        message1.Should().Be(message2);
    }
}

public class ResultTests
{
    [Fact]
    public void Success_WithValue_ReturnsSuccessResult()
    {
        var result = Result<int, string>.Success(42);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(42);
    }

    [Fact]
    public void Failure_WithError_ReturnsFailureResult()
    {
        var result = Result<int, string>.Failure("something went wrong");

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be("something went wrong");
    }

    [Fact]
    public void Success_ErrorShouldBeDefault()
    {
        var result = Result<int, string>.Success(42);

        result.Error.Should().BeNull();
    }

    [Fact]
    public void Failure_ValueShouldBeDefault()
    {
        var result = Result<int, string>.Failure("error");

        result.Value.Should().Be(default);
    }
}

public class ActivityEventTests
{
    [Fact]
    public void Constructor_WithValidParameters_CreatesEvent()
    {
        var id = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow;
        var source = new Address("agent", "engineering-team/ada");

        var activityEvent = new ActivityEvent(id, timestamp, source, "TaskCompleted", "Agent completed the task");

        activityEvent.Id.Should().Be(id);
        activityEvent.Timestamp.Should().Be(timestamp);
        activityEvent.Source.Should().Be(source);
        activityEvent.EventType.Should().Be("TaskCompleted");
        activityEvent.Description.Should().Be("Agent completed the task");
    }

    [Fact]
    public void Equals_SameValues_ReturnsTrue()
    {
        var id = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow;
        var source = new Address("agent", "engineering-team/ada");

        var event1 = new ActivityEvent(id, timestamp, source, "TaskCompleted", "Done");
        var event2 = new ActivityEvent(id, timestamp, source, "TaskCompleted", "Done");

        event1.Should().Be(event2);
    }
}

public class InterfaceHierarchyTests
{
    [Fact]
    public void IMessageReceiver_ExtendsIAddressable()
    {
        typeof(IMessageReceiver).GetInterfaces().Should().Contain(typeof(IAddressable));
    }
}
