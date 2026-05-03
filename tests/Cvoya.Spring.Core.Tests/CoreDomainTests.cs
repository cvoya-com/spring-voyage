// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;

using Shouldly;

using Xunit;

// Post #1629: Address is `(Scheme, Guid Id)` only — slug paths,
// `IsIdentity`, `ForAgent`/`ForUnit` slug factories and `ToIdentityUri`
// have been removed. Wire form is `scheme:<32-hex-no-dash>`; lenient
// parsing accepts dashed Guids too.
public class AddressTests
{
    private static readonly Guid AdaId = Guid.Parse("1f9e3c2d-0000-0000-0000-000000000001");
    private static readonly Guid BobId = Guid.Parse("1f9e3c2d-0000-0000-0000-000000000002");
    private static readonly Guid EngineeringId = Guid.Parse("2a3b4c5d-0000-0000-0000-000000000003");

    [Fact]
    public void Equals_SameSchemeAndId_ReturnsTrue()
    {
        var address1 = new Address("agent", AdaId);
        var address2 = new Address("agent", AdaId);

        address1.ShouldBe(address2);
    }

    [Fact]
    public void Equals_DifferentScheme_ReturnsFalse()
    {
        var address1 = new Address("agent", AdaId);
        var address2 = new Address("unit", AdaId);

        address1.ShouldNotBe(address2);
    }

    [Fact]
    public void Equals_DifferentId_ReturnsFalse()
    {
        var address1 = new Address("agent", AdaId);
        var address2 = new Address("agent", BobId);

        address1.ShouldNotBe(address2);
    }

    [Fact]
    public void GetHashCode_EqualAddresses_ReturnsSameHashCode()
    {
        var address1 = new Address("agent", AdaId);
        var address2 = new Address("agent", AdaId);

        address1.GetHashCode().ShouldBe(address2.GetHashCode());
    }

    [Fact]
    public void ToString_ReturnsSchemeColonNoDashHex()
    {
        var address = new Address("agent", AdaId);

        address.ToString().ShouldBe($"agent:{AdaId:N}");
    }

    [Fact]
    public void ToString_InterpolatedIntoString_ReturnsSchemeColonNoDashHex()
    {
        var address = new Address("agent", AdaId);

        $"from {address}".ShouldBe($"from agent:{AdaId:N}");
    }

    [Fact]
    public void ToCanonicalUri_AliasOfToString()
    {
        var address = new Address("unit", EngineeringId);

        address.ToCanonicalUri().ShouldBe(address.ToString());
        address.ToCanonicalUri().ShouldBe($"unit:{EngineeringId:N}");
    }

    [Fact]
    public void Path_ReturnsNoDashHex()
    {
        new Address("agent", AdaId).Path.ShouldBe(AdaId.ToString("N"));
    }

    [Fact]
    public void For_ParsesDashedGuidString()
    {
        var address = Address.For("agent", AdaId.ToString());
        address.Scheme.ShouldBe("agent");
        address.Id.ShouldBe(AdaId);
    }

    [Fact]
    public void For_ParsesNoDashGuidString()
    {
        var address = Address.For("agent", AdaId.ToString("N"));
        address.Id.ShouldBe(AdaId);
    }

    [Fact]
    public void For_NonGuidIdString_Throws()
    {
        Should.Throw<ArgumentException>(() => Address.For("agent", "not-a-uuid"));
    }

    [Fact]
    public void ForIdentity_BuildsAddressFromGuid()
    {
        var address = Address.ForIdentity("agent", AdaId);
        address.Scheme.ShouldBe("agent");
        address.Id.ShouldBe(AdaId);
    }

    [Fact]
    public void TryParse_DashedForm_Succeeds()
    {
        var ok = Address.TryParse($"agent:{AdaId}", out var address);

        ok.ShouldBeTrue();
        address.ShouldNotBeNull();
        address!.Scheme.ShouldBe("agent");
        address.Id.ShouldBe(AdaId);
    }

    [Fact]
    public void TryParse_NoDashForm_Succeeds()
    {
        var ok = Address.TryParse($"agent:{AdaId:N}", out var address);

        ok.ShouldBeTrue();
        address!.Scheme.ShouldBe("agent");
        address.Id.ShouldBe(AdaId);
    }

    [Fact]
    public void TryParse_UnitScheme_Succeeds()
    {
        var ok = Address.TryParse($"unit:{EngineeringId:N}", out var address);

        ok.ShouldBeTrue();
        address!.Scheme.ShouldBe("unit");
        address.Id.ShouldBe(EngineeringId);
    }

    [Fact]
    public void TryParse_NonGuidId_ReturnsFalse()
    {
        // Slug-shaped paths are no longer addresses post #1629.
        Address.TryParse("agent:not-a-uuid", out _).ShouldBeFalse();
    }

    [Fact]
    public void TryParse_EmptyString_ReturnsFalse()
    {
        Address.TryParse(string.Empty, out _).ShouldBeFalse();
    }

    [Fact]
    public void TryParse_NoSeparator_ReturnsFalse()
    {
        Address.TryParse("justalabel", out _).ShouldBeFalse();
    }

    [Fact]
    public void RoundTrip_ParseFormatParse()
    {
        // Construct → ToString (canonical no-dash) → parse back → same value.
        var id = Guid.Parse("abcdef12-1234-5678-9abc-def012345678");
        var original = new Address("agent", id);
        var uri = original.ToString();

        var ok = Address.TryParse(uri, out var parsed);

        ok.ShouldBeTrue();
        parsed!.Scheme.ShouldBe("agent");
        parsed.Id.ShouldBe(id);
        parsed.ShouldBe(original);
    }
}

public class MessageTests
{
    [Fact]
    public void Constructor_WithValidParameters_CreatesMessage()
    {
        var id = Guid.NewGuid();
        var from = new Address("agent", Guid.NewGuid());
        var to = new Address("agent", Guid.NewGuid());
        var payload = JsonDocument.Parse("{\"key\":\"value\"}").RootElement;
        var timestamp = DateTimeOffset.UtcNow;

        var message = new Message(id, from, to, MessageType.Domain, "conv-1", payload, timestamp);

        message.Id.ShouldBe(id);
        message.From.ShouldBe(from);
        message.To.ShouldBe(to);
        message.Type.ShouldBe(MessageType.Domain);
        message.ThreadId.ShouldBe("conv-1");
        message.Payload.GetProperty("key").GetString().ShouldBe("value");
        message.Timestamp.ShouldBe(timestamp);
    }

    [Fact]
    public void Constructor_WithNullThreadId_CreatesMessage()
    {
        var payload = JsonDocument.Parse("{}").RootElement;

        var message = new Message(
            Guid.NewGuid(),
            new Address("agent", Guid.NewGuid()),
            new Address("agent", Guid.NewGuid()),
            MessageType.HealthCheck,
            null,
            payload,
            DateTimeOffset.UtcNow);

        message.ThreadId.ShouldBeNull();
    }

    [Fact]
    public void Equals_SameValues_ReturnsTrue()
    {
        var id = Guid.NewGuid();
        var from = new Address("agent", Guid.NewGuid());
        var to = new Address("agent", Guid.NewGuid());
        var payload = JsonDocument.Parse("{}").RootElement;
        var timestamp = DateTimeOffset.UtcNow;

        var message1 = new Message(id, from, to, MessageType.Domain, "conv-1", payload, timestamp);
        var message2 = new Message(id, from, to, MessageType.Domain, "conv-1", payload, timestamp);

        message1.ShouldBe(message2);
    }
}

public class ResultTests
{
    [Fact]
    public void Success_WithValue_ReturnsSuccessResult()
    {
        var result = Result<int, string>.Success(42);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
    }

    [Fact]
    public void Failure_WithError_ReturnsFailureResult()
    {
        var result = Result<int, string>.Failure("something went wrong");

        result.IsSuccess.ShouldBeFalse();
        result.Error.ShouldBe("something went wrong");
    }

    [Fact]
    public void Success_ErrorShouldBeDefault()
    {
        var result = Result<int, string>.Success(42);

        result.Error.ShouldBeNull();
    }

    [Fact]
    public void Failure_ValueShouldBeDefault()
    {
        var result = Result<int, string>.Failure("error");

        result.Value.ShouldBe(default);
    }
}

public class ActivityEventTests
{
    [Fact]
    public void Constructor_WithRequiredParameters_CreatesEvent()
    {
        var id = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow;
        var source = new Address("agent", Guid.NewGuid());

        var activityEvent = new ActivityEvent(
            id, timestamp, source,
            ActivityEventType.ThreadCompleted,
            ActivitySeverity.Info,
            "Agent completed the task");

        activityEvent.Id.ShouldBe(id);
        activityEvent.Timestamp.ShouldBe(timestamp);
        activityEvent.Source.ShouldBe(source);
        activityEvent.EventType.ShouldBe(ActivityEventType.ThreadCompleted);
        activityEvent.Severity.ShouldBe(ActivitySeverity.Info);
        activityEvent.Summary.ShouldBe("Agent completed the task");
        activityEvent.Details.ShouldBeNull();
        activityEvent.CorrelationId.ShouldBeNull();
        activityEvent.Cost.ShouldBeNull();
    }

    [Fact]
    public void Constructor_WithAllParameters_CreatesEvent()
    {
        var id = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow;
        var source = new Address("agent", Guid.NewGuid());
        var details = JsonDocument.Parse("{\"tokens\":150}").RootElement;

        var activityEvent = new ActivityEvent(
            id, timestamp, source,
            ActivityEventType.CostIncurred,
            ActivitySeverity.Debug,
            "Token usage recorded",
            details,
            "corr-123",
            0.0042m);

        activityEvent.Details.ShouldNotBeNull();
        activityEvent.Details!.Value.GetProperty("tokens").GetInt32().ShouldBe(150);
        activityEvent.CorrelationId.ShouldBe("corr-123");
        activityEvent.Cost.ShouldBe(0.0042m);
    }

    [Fact]
    public void Equals_SameValues_ReturnsTrue()
    {
        var id = Guid.NewGuid();
        var timestamp = DateTimeOffset.UtcNow;
        var source = new Address("agent", Guid.NewGuid());

        var event1 = new ActivityEvent(id, timestamp, source, ActivityEventType.MessageSent, ActivitySeverity.Info, "Done");
        var event2 = new ActivityEvent(id, timestamp, source, ActivityEventType.MessageSent, ActivitySeverity.Info, "Done");

        event1.ShouldBe(event2);
    }
}

public class InterfaceHierarchyTests
{
    [Fact]
    public void IMessageReceiver_ExtendsIAddressable()
    {
        typeof(IMessageReceiver).GetInterfaces().ShouldContain(typeof(IAddressable));
    }
}