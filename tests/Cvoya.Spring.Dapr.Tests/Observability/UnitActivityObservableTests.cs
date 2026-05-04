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
                Address.For("agent", TestSlugIds.HexFor("agent-a")),
                Address.For("agent", TestSlugIds.HexFor("agent-b")),
            });
        _proxyFactory.CreateActorProxy<IUnitActor>(new ActorId(TestSlugIds.HexFor("unit-1")), nameof(UnitActor))
            .Returns(unit);

        var sut = CreateSut();
        var stream = await sut.GetStreamAsync(TestSlugIds.HexFor("unit-1"), TestContext.Current.CancellationToken);

        var observed = new List<ActivityEvent>();
        using var subscription = stream.Subscribe(observed.Add);

        _bus.Publish(Evt(Address.For("agent", TestSlugIds.HexFor("agent-a")), ActivityEventType.MessageReceived));
        _bus.Publish(Evt(Address.For("agent", TestSlugIds.HexFor("agent-c")), ActivityEventType.MessageReceived));
        _bus.Publish(Evt(Address.For("unit", TestSlugIds.HexFor("unit-1")), ActivityEventType.DecisionMade));
        _bus.Publish(Evt(Address.For("agent", TestSlugIds.HexFor("agent-b")), ActivityEventType.TokenDelta));

        observed.Count.ShouldBe(3);
        observed[0].Source.Path.ShouldBe(TestSlugIds.HexFor("agent-a"));
        observed[1].Source.Path.ShouldBe(TestSlugIds.HexFor("unit-1"));
        observed[2].Source.Path.ShouldBe(TestSlugIds.HexFor("agent-b"));
    }

    [Fact]
    public async Task GetStreamAsync_WithNestedSubUnit_WalksTransitively()
    {
        // Parent unit-1 contains sub-unit unit-2 contains agent-z.
        var parent = Substitute.For<IUnitActor>();
        parent.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { Address.For("unit", TestSlugIds.HexFor("unit-2")) });
        _proxyFactory.CreateActorProxy<IUnitActor>(new ActorId(TestSlugIds.HexFor("unit-1")), nameof(UnitActor))
            .Returns(parent);

        var sub = Substitute.For<IUnitActor>();
        sub.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { Address.For("agent", TestSlugIds.HexFor("agent-z")) });
        _proxyFactory.CreateActorProxy<IUnitActor>(new ActorId(TestSlugIds.HexFor("unit-2")), nameof(UnitActor))
            .Returns(sub);

        var unit2Id = TestSlugIds.For("unit-2");
        _directory.ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                Address: new Address("unit", unit2Id),
                ActorId: unit2Id,
                DisplayName: "unit-2",
                Description: string.Empty,
                Role: null,
                RegisteredAt: DateTimeOffset.UtcNow));

        var sut = CreateSut();
        var stream = await sut.GetStreamAsync(TestSlugIds.HexFor("unit-1"), TestContext.Current.CancellationToken);

        var observed = new List<ActivityEvent>();
        using var subscription = stream.Subscribe(observed.Add);

        _bus.Publish(Evt(Address.For("agent", TestSlugIds.HexFor("agent-z")), ActivityEventType.ToolCall));
        _bus.Publish(Evt(Address.For("unit", TestSlugIds.HexFor("unit-2")), ActivityEventType.StateChanged));
        _bus.Publish(Evt(Address.For("agent", TestSlugIds.HexFor("agent-not-mine")), ActivityEventType.MessageReceived));

        observed.Count.ShouldBe(2);
        observed.ShouldContain(e => e.Source.Path == TestSlugIds.HexFor("agent-z"));
        observed.ShouldContain(e => e.Source.Path == TestSlugIds.HexFor("unit-2"));
    }

    [Fact]
    public async Task GetStreamAsync_UnknownUnit_ReturnsEmptyStream()
    {
        var unit = Substitute.For<IUnitActor>();
        unit.GetMembersAsync(Arg.Any<CancellationToken>())
            .Returns(System.Array.Empty<Address>());
        _proxyFactory.CreateActorProxy<IUnitActor>(new ActorId(TestSlugIds.HexFor("unit-none")), nameof(UnitActor))
            .Returns(unit);

        var sut = CreateSut();
        var stream = await sut.GetStreamAsync(TestSlugIds.HexFor("unit-none"), TestContext.Current.CancellationToken);

        var observed = new List<ActivityEvent>();
        using var subscription = stream.Subscribe(observed.Add);

        _bus.Publish(Evt(Address.For("agent", TestSlugIds.HexFor("agent-x")), ActivityEventType.MessageReceived));

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