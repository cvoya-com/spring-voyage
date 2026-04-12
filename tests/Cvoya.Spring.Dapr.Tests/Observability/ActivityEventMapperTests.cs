// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Observability;

using FluentAssertions;

using Xunit;

public class ActivityEventMapperTests
{
    [Fact]
    public void ToRecord_MapsAllFields()
    {
        var details = JsonDocument.Parse("{\"key\":\"value\"}").RootElement;
        var activityEvent = new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new Address("agent", "team/ada"),
            ActivityEventType.DecisionMade,
            ActivitySeverity.Debug,
            "Decision recorded",
            details,
            "corr-456",
            1.23m);

        var record = ActivityEventMapper.ToRecord(activityEvent);

        record.Id.Should().Be(activityEvent.Id);
        record.Source.Should().Be("agent:team/ada");
        record.EventType.Should().Be("DecisionMade");
        record.Severity.Should().Be("Debug");
        record.Summary.Should().Be("Decision recorded");
        record.Details!.Value.GetProperty("key").GetString().Should().Be("value");
        record.CorrelationId.Should().Be("corr-456");
        record.Cost.Should().Be(1.23m);
        record.Timestamp.Should().Be(activityEvent.Timestamp);
    }

    [Fact]
    public void ToDomain_MapsAllFields()
    {
        var details = JsonDocument.Parse("{\"tokens\":42}").RootElement;
        var record = new ActivityEventRecord
        {
            Id = Guid.NewGuid(),
            Source = "unit:engineering",
            EventType = "CostIncurred",
            Severity = "Warning",
            Summary = "High cost",
            Details = details,
            CorrelationId = "corr-789",
            Cost = 5.67m,
            Timestamp = DateTimeOffset.UtcNow,
        };

        var domain = ActivityEventMapper.ToDomain(record);

        domain.Id.Should().Be(record.Id);
        domain.Source.Should().Be(new Address("unit", "engineering"));
        domain.EventType.Should().Be(ActivityEventType.CostIncurred);
        domain.Severity.Should().Be(ActivitySeverity.Warning);
        domain.Summary.Should().Be("High cost");
        domain.Details!.Value.GetProperty("tokens").GetInt32().Should().Be(42);
        domain.CorrelationId.Should().Be("corr-789");
        domain.Cost.Should().Be(5.67m);
        domain.Timestamp.Should().Be(record.Timestamp);
    }

    [Fact]
    public void Roundtrip_ToRecordThenToDomain_PreservesValues()
    {
        var original = new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new Address("agent", "test-agent"),
            ActivityEventType.MessageReceived,
            ActivitySeverity.Info,
            "Message received",
            null,
            "corr-roundtrip",
            null);

        var record = ActivityEventMapper.ToRecord(original);
        var restored = ActivityEventMapper.ToDomain(record);

        restored.Id.Should().Be(original.Id);
        restored.Source.Should().Be(original.Source);
        restored.EventType.Should().Be(original.EventType);
        restored.Severity.Should().Be(original.Severity);
        restored.Summary.Should().Be(original.Summary);
        restored.Details.Should().Be(original.Details);
        restored.CorrelationId.Should().Be(original.CorrelationId);
        restored.Cost.Should().Be(original.Cost);
        restored.Timestamp.Should().Be(original.Timestamp);
    }

    [Fact]
    public void ToRecord_NullOptionalFields_MapsCorrectly()
    {
        var activityEvent = new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new Address("agent", "test"),
            ActivityEventType.StateChanged,
            ActivitySeverity.Info,
            "State changed");

        var record = ActivityEventMapper.ToRecord(activityEvent);

        record.Details.Should().BeNull();
        record.CorrelationId.Should().BeNull();
        record.Cost.Should().BeNull();
    }
}