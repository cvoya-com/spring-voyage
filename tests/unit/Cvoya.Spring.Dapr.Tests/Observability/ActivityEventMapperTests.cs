// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Observability;

using Shouldly;

using Xunit;

public class ActivityEventMapperTests
{
    private static readonly Guid AdaId = new("aaaaaaaa-1111-1111-1111-000000000001");
    private static readonly Guid EngineeringId = new("bbbbbbbb-2222-2222-2222-000000000001");
    private static readonly Guid TestAgentId = new("aaaaaaaa-1111-1111-1111-000000000002");

    [Fact]
    public void ToRecord_MapsAllFields()
    {
        var details = JsonDocument.Parse("{\"key\":\"value\"}").RootElement;
        var activityEvent = new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new Address("agent", AdaId),
            ActivityEventType.DecisionMade,
            ActivitySeverity.Debug,
            "Decision recorded",
            details,
            "corr-456",
            1.23m);

        var record = ActivityEventMapper.ToRecord(activityEvent);

        record.Id.ShouldBe(activityEvent.Id);
        record.SourceId.ShouldBe(AdaId);
        record.EventType.ShouldBe("DecisionMade");
        record.Severity.ShouldBe("Debug");
        record.Summary.ShouldBe("Decision recorded");
        record.Details!.Value.GetProperty("key").GetString().ShouldBe("value");
        record.CorrelationId.ShouldBe("corr-456");
        record.Cost.ShouldBe(1.23m);
        record.Timestamp.ShouldBe(activityEvent.Timestamp);
    }

    [Fact]
    public void ToDomain_MapsAllFields()
    {
        var details = JsonDocument.Parse("{\"tokens\":42}").RootElement;
        var record = new ActivityEventRecord
        {
            Id = Guid.NewGuid(),
            SourceId = EngineeringId,
            EventType = "CostIncurred",
            Severity = "Warning",
            Summary = "High cost",
            Details = details,
            CorrelationId = "corr-789",
            Cost = 5.67m,
            Timestamp = DateTimeOffset.UtcNow,
        };

        var domain = ActivityEventMapper.ToDomain(record);

        domain.Id.ShouldBe(record.Id);
        domain.Source.Id.ShouldBe(EngineeringId);
        domain.EventType.ShouldBe(ActivityEventType.CostIncurred);
        domain.Severity.ShouldBe(ActivitySeverity.Warning);
        domain.Summary.ShouldBe("High cost");
        domain.Details!.Value.GetProperty("tokens").GetInt32().ShouldBe(42);
        domain.CorrelationId.ShouldBe("corr-789");
        domain.Cost.ShouldBe(5.67m);
        domain.Timestamp.ShouldBe(record.Timestamp);
    }

    [Fact]
    public void Roundtrip_ToRecordThenToDomain_PreservesValues()
    {
        var original = new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new Address("agent", TestAgentId),
            ActivityEventType.MessageReceived,
            ActivitySeverity.Info,
            "Message received",
            null,
            "corr-roundtrip",
            null);

        var record = ActivityEventMapper.ToRecord(original);
        var restored = ActivityEventMapper.ToDomain(record);

        restored.Id.ShouldBe(original.Id);
        restored.Source.Id.ShouldBe(original.Source.Id);
        restored.EventType.ShouldBe(original.EventType);
        restored.Severity.ShouldBe(original.Severity);
        restored.Summary.ShouldBe(original.Summary);
        restored.Details.ShouldBe(original.Details);
        restored.CorrelationId.ShouldBe(original.CorrelationId);
        restored.Cost.ShouldBe(original.Cost);
        restored.Timestamp.ShouldBe(original.Timestamp);
    }

    [Fact]
    public void ToRecord_NullOptionalFields_MapsCorrectly()
    {
        var activityEvent = new ActivityEvent(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            new Address("agent", TestAgentId),
            ActivityEventType.StateChanged,
            ActivitySeverity.Info,
            "State changed");

        var record = ActivityEventMapper.ToRecord(activityEvent);

        record.Details.ShouldBeNull();
        record.CorrelationId.ShouldBeNull();
        record.Cost.ShouldBeNull();
    }
}
