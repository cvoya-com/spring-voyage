// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Observability;

using Shouldly;

using Xunit;

public class ActivityBusTests
{
    [Fact]
    public void Publish_WithSubscriber_DeliversEvent()
    {
        using var bus = new ActivityEventBus();
        var received = new List<ActivityEvent>();
        using var sub = bus.ActivityStream.Subscribe(received.Add);

        var evt = new ActivityEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow,
            Address.For("agent", TestSlugIds.HexFor("test")),
            ActivityEventType.WorkflowStepCompleted, ActivitySeverity.Info, "Done");
        bus.Publish(evt);

        received.ShouldHaveSingleItem().ShouldBe(evt);
    }

    [Fact]
    public void Publish_MultipleSubscribers_AllReceiveEvent()
    {
        using var bus = new ActivityEventBus();
        var received1 = new List<ActivityEvent>();
        var received2 = new List<ActivityEvent>();
        using var sub1 = bus.ActivityStream.Subscribe(received1.Add);
        using var sub2 = bus.ActivityStream.Subscribe(received2.Add);

        var evt = new ActivityEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow,
            Address.For("agent", TestSlugIds.HexFor("test")),
            ActivityEventType.WorkflowStepCompleted, ActivitySeverity.Info, "Done");
        bus.Publish(evt);

        received1.ShouldHaveSingleItem();
        received2.ShouldHaveSingleItem();
    }

    [Fact]
    public void Publish_AfterUnsubscribe_DoesNotDeliver()
    {
        using var bus = new ActivityEventBus();
        var received = new List<ActivityEvent>();
        var sub = bus.ActivityStream.Subscribe(received.Add);
        sub.Dispose();

        var evt = new ActivityEvent(
            Guid.NewGuid(), DateTimeOffset.UtcNow,
            Address.For("agent", TestSlugIds.HexFor("test")),
            ActivityEventType.WorkflowStepCompleted, ActivitySeverity.Info, "Done");
        bus.Publish(evt);

        received.ShouldBeEmpty();
    }
}