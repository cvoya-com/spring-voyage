// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;

using Shouldly;

using Xunit;

public class AddressTests
{
    [Fact]
    public void Equals_SameSchemeAndPath_ReturnsTrue()
    {
        var address1 = new Address("agent", "engineering-team/ada");
        var address2 = new Address("agent", "engineering-team/ada");

        address1.ShouldBe(address2);
    }

    [Fact]
    public void Equals_DifferentScheme_ReturnsFalse()
    {
        var address1 = new Address("agent", "engineering-team/ada");
        var address2 = new Address("unit", "engineering-team/ada");

        address1.ShouldNotBe(address2);
    }

    [Fact]
    public void Equals_DifferentPath_ReturnsFalse()
    {
        var address1 = new Address("agent", "engineering-team/ada");
        var address2 = new Address("agent", "engineering-team/bob");

        address1.ShouldNotBe(address2);
    }

    [Fact]
    public void GetHashCode_EqualAddresses_ReturnsSameHashCode()
    {
        var address1 = new Address("agent", "engineering-team/ada");
        var address2 = new Address("agent", "engineering-team/ada");

        address1.GetHashCode().ShouldBe(address2.GetHashCode());
    }

    [Fact]
    public void ToString_ReturnsSchemeColonPath()
    {
        var address = new Address("human", "local-dev-user");

        address.ToString().ShouldBe("human:local-dev-user");
    }

    [Fact]
    public void ToString_InterpolatedIntoString_ReturnsSchemeColonPath()
    {
        var address = new Address("agent", "engineering-team/ada");

        $"from {address}".ShouldBe("from agent:engineering-team/ada");
    }

    // #1060: ToCanonicalUri produces the scheme://path form used by wire
    // projections (member field on UnitMembershipResponse, source column on
    // activity / cost rows). Distinct from ToString, which uses ":" for
    // log lines and error messages.
    [Fact]
    public void ToCanonicalUri_AgentScheme_ReturnsSchemeSlashSlashPath()
    {
        new Address("agent", "engineering-team/ada")
            .ToCanonicalUri()
            .ShouldBe("agent://engineering-team/ada");
    }

    [Fact]
    public void ToCanonicalUri_UnitScheme_ReturnsSchemeSlashSlashPath()
    {
        new Address("unit", "engineering-team")
            .ToCanonicalUri()
            .ShouldBe("unit://engineering-team");
    }

    [Fact]
    public void ToCanonicalUri_RoundTripsThroughForAgent()
    {
        Address.ForAgent("ada").ToCanonicalUri().ShouldBe("agent://ada");
    }

    [Fact]
    public void ToCanonicalUri_RoundTripsThroughForUnit()
    {
        Address.ForUnit("engineering").ToCanonicalUri().ShouldBe("unit://engineering");
    }

    [Fact]
    public void ForAgent_PopulatesAgentScheme()
    {
        var address = Address.ForAgent("ada");
        address.Scheme.ShouldBe("agent");
        address.Path.ShouldBe("ada");
    }

    [Fact]
    public void ForUnit_PopulatesUnitScheme()
    {
        var address = Address.ForUnit("engineering");
        address.Scheme.ShouldBe("unit");
        address.Path.ShouldBe("engineering");
    }

    // --- Identity form tests (#1490) ---

    [Fact]
    public void ForIdentity_SetsIsIdentityTrue()
    {
        var id = Guid.Parse("1f9e3c2d-0000-0000-0000-000000000001");
        var address = Address.ForIdentity("agent", id);

        address.Scheme.ShouldBe("agent");
        address.Path.ShouldBe(id.ToString());
        address.IsIdentity.ShouldBeTrue();
    }

    [Fact]
    public void ToIdentityUri_IdentityAddress_ReturnsSchemeIdUuid()
    {
        var id = Guid.Parse("1f9e3c2d-0000-0000-0000-000000000001");
        var address = Address.ForIdentity("agent", id);

        address.ToIdentityUri().ShouldBe($"agent:id:{id}");
    }

    [Fact]
    public void ToCanonicalUri_IdentityAddress_DelegatesToToIdentityUri()
    {
        var id = Guid.Parse("1f9e3c2d-0000-0000-0000-000000000002");
        var address = Address.ForIdentity("unit", id);

        address.ToCanonicalUri().ShouldBe($"unit:id:{id}");
    }

    [Fact]
    public void ToIdentityUri_NavigationAddress_ThrowsInvalidOperationException()
    {
        var address = Address.ForAgent("ada");

        Should.Throw<InvalidOperationException>(() => address.ToIdentityUri());
    }

    [Fact]
    public void ToString_IdentityAddress_ReturnsSchemeIdUuid()
    {
        var id = Guid.Parse("1f9e3c2d-0000-0000-0000-000000000001");
        var address = Address.ForIdentity("agent", id);

        address.ToString().ShouldBe($"agent:id:{id}");
    }

    [Fact]
    public void TryParse_NavigationForm_ReturnsTrueAndNavigationAddress()
    {
        var ok = Address.TryParse("agent://engineering/ada", out var address);

        ok.ShouldBeTrue();
        address.ShouldNotBeNull();
        address!.Scheme.ShouldBe("agent");
        address.Path.ShouldBe("engineering/ada");
        address.IsIdentity.ShouldBeFalse();
    }

    [Fact]
    public void TryParse_IdentityForm_ReturnsTrueAndIdentityAddress()
    {
        var id = Guid.Parse("1f9e3c2d-0000-0000-0000-000000000001");
        var ok = Address.TryParse($"agent:id:{id}", out var address);

        ok.ShouldBeTrue();
        address.ShouldNotBeNull();
        address!.Scheme.ShouldBe("agent");
        address.Path.ShouldBe(id.ToString());
        address.IsIdentity.ShouldBeTrue();
    }

    [Fact]
    public void TryParse_IdentityForm_UnitScheme_Works()
    {
        var id = Guid.Parse("2a3b4c5d-0000-0000-0000-000000000002");
        var ok = Address.TryParse($"unit:id:{id}", out var address);

        ok.ShouldBeTrue();
        address!.Scheme.ShouldBe("unit");
        address.IsIdentity.ShouldBeTrue();
    }

    [Fact]
    public void TryParse_IdentityFormNotAUuid_ReturnsFalse()
    {
        // The path after ":id:" must be a valid UUID; a slug-shaped value is rejected.
        var ok = Address.TryParse("agent:id:not-a-uuid", out _);

        ok.ShouldBeFalse();
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
    public void IdentityAddress_RoundTrip_ParseFormatParse()
    {
        // Construct → ToIdentityUri → parse back → same value.
        var id = Guid.Parse("abcdef12-1234-5678-9abc-def012345678");
        var original = Address.ForIdentity("agent", id);
        var uri = original.ToIdentityUri();

        var ok = Address.TryParse(uri, out var parsed);

        ok.ShouldBeTrue();
        parsed!.Scheme.ShouldBe("agent");
        parsed.Path.ShouldBe(id.ToString());
        parsed.IsIdentity.ShouldBeTrue();
        parsed.ShouldBe(original);
    }

    [Fact]
    public void NavigationAddress_RoundTrip_ParseFormatParse()
    {
        // Construct → ToCanonicalUri → parse back → same value.
        var original = Address.ForAgent("engineering/ada");
        var uri = original.ToCanonicalUri();

        var ok = Address.TryParse(uri, out var parsed);

        ok.ShouldBeTrue();
        parsed!.Scheme.ShouldBe("agent");
        parsed.Path.ShouldBe("engineering/ada");
        parsed.IsIdentity.ShouldBeFalse();
        parsed.ShouldBe(original);
    }

    [Fact]
    public void NavigationAddress_IsIdentity_DefaultsFalse()
    {
        var address = new Address("agent", "ada");
        address.IsIdentity.ShouldBeFalse();
    }

    [Fact]
    public void IdentityAndNavigationAddresses_WithSameSchemeAndPath_AreNotEqual()
    {
        // The uuid-shaped slug "1f9e3c2d-..." stored in a navigation address
        // must NOT equal an identity address for the same uuid. This is the
        // core disambiguation that #1490 ships.
        var id = Guid.Parse("1f9e3c2d-0000-0000-0000-000000000001");
        var navAddress = new Address("agent", id.ToString(), IsIdentity: false);
        var idAddress = Address.ForIdentity("agent", id);

        navAddress.ShouldNotBe(idAddress);
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
            new Address("agent", "sender"),
            new Address("agent", "receiver"),
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
        var from = new Address("agent", "sender");
        var to = new Address("agent", "receiver");
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
        var source = new Address("agent", "engineering-team/ada");

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
        var source = new Address("agent", "engineering-team/ada");
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
        var source = new Address("agent", "engineering-team/ada");

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