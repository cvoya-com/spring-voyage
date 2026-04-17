// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Observability;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Observability;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.Logging.Abstractions;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Exercises the unit-scoped activity observable — the
/// <c>Observable.Merge(unit.Members.Select(m =&gt; m.ActivityStream))</c>
/// acceptance item of issue #391. Tests use a real
/// <see cref="ActivityEventBus"/> so the filtering semantics exercise the
/// same Rx pipeline production uses.
/// </summary>
public class UnitActivityObservableTests : IDisposable
{
    private readonly ActivityEventBus _bus = new();
    private readonly IActorProxyFactory _proxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly IDirectoryService _directory = Substitute.For<IDirectoryService>();

    private UnitActivityObservable CreateSut() => new(
        _bus,
        _proxyFactory,
        _directory,
        NullLoggerFactory.Instance);

    [Fact]
    public async Task GetStreamAsync_WithDirectAgents_EmitsOnlyMemberEvents()
    {
        var unit = Substitute.For<IUnitActor>();
        unit.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new Address("agent", "agent-a"),
                new Address("agent", "agent-b"),
            });
        _proxyFactory.CreateActorProxy<IUnitActor>(new ActorId("unit-1"), nameof(UnitActor))
            .Returns(unit);

        var sut = CreateSut();
        var stream = await sut.GetStreamAsync("unit-1", TestContext.Current.CancellationToken);

        var observed = new List<ActivityEvent>();
        using var subscription = stream.Subscribe(observed.Add);

        _bus.Publish(Evt(new Address("agent", "agent-a"), ActivityEventType.MessageReceived));
        _bus.Publish(Evt(new Address("agent", "agent-c"), ActivityEventType.MessageReceived));
        _bus.Publish(Evt(new Address("unit", "unit-1"), ActivityEventType.DecisionMade));
        _bus.Publish(Evt(new Address("agent", "agent-b"), ActivityEventType.TokenDelta));

        observed.Count.ShouldBe(3);
        observed[0].Source.Path.ShouldBe("agent-a");
        observed[1].Source.Path.ShouldBe("unit-1");
        observed[2].Source.Path.ShouldBe("agent-b");
    }

    [Fact]
    public async Task GetStreamAsync_WithNestedSubUnit_WalksTransitively()
    {
        // Parent unit-1 contains sub-unit unit-2 contains agent-z.
        var parent = Substitute.For<IUnitActor>();
        parent.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new Address("unit", "unit-2") });
        _proxyFactory.CreateActorProxy<IUnitActor>(new ActorId("unit-1"), nameof(UnitActor))
            .Returns(parent);

        var sub = Substitute.For<IUnitActor>();
        sub.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { new Address("agent", "agent-z") });
        _proxyFactory.CreateActorProxy<IUnitActor>(new ActorId("unit-2"), nameof(UnitActor))
            .Returns(sub);

        _directory.ResolveAsync(new Address("unit", "unit-2"), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                Address: new Address("unit", "unit-2"),
                ActorId: "unit-2",
                DisplayName: "unit-2",
                Description: string.Empty,
                Role: null,
                RegisteredAt: DateTimeOffset.UtcNow));

        var sut = CreateSut();
        var stream = await sut.GetStreamAsync("unit-1", TestContext.Current.CancellationToken);

        var observed = new List<ActivityEvent>();
        using var subscription = stream.Subscribe(observed.Add);

        _bus.Publish(Evt(new Address("agent", "agent-z"), ActivityEventType.ToolCall));
        _bus.Publish(Evt(new Address("unit", "unit-2"), ActivityEventType.StateChanged));
        _bus.Publish(Evt(new Address("agent", "agent-not-mine"), ActivityEventType.MessageReceived));

        observed.Count.ShouldBe(2);
        observed.ShouldContain(e => e.Source.Path == "agent-z");
        observed.ShouldContain(e => e.Source.Path == "unit-2");
    }

    [Fact]
    public async Task GetStreamAsync_UnknownUnit_ReturnsEmptyStream()
    {
        var unit = Substitute.For<IUnitActor>();
        unit.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(System.Array.Empty<Address>());
        _proxyFactory.CreateActorProxy<IUnitActor>(new ActorId("unit-none"), nameof(UnitActor))
            .Returns(unit);

        var sut = CreateSut();
        var stream = await sut.GetStreamAsync("unit-none", TestContext.Current.CancellationToken);

        var observed = new List<ActivityEvent>();
        using var subscription = stream.Subscribe(observed.Add);

        _bus.Publish(Evt(new Address("agent", "agent-x"), ActivityEventType.MessageReceived));

        // Unit itself is always included in the member set, so an event
        // published from it would land here. No such event above → empty.
        observed.ShouldBeEmpty();
    }

    private static ActivityEvent Evt(Address source, ActivityEventType type) =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            source,
            type,
            ActivitySeverity.Info,
            Summary: $"{type}");

    public void Dispose()
    {
        _bus.Dispose();
    }
}