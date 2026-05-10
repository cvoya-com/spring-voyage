// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Tests.TestHelpers;
using Cvoya.Spring.Dapr.Units;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for <see cref="UnitActor.GetBoundaryAsync"/> and
/// <see cref="UnitActor.SetBoundaryAsync"/> (#413). Covers empty-state
/// defaults, upsert-then-read, and the "empty boundary clears state"
/// semantics. Per ADR-0040 / #2049 the boundary lives on
/// <c>unit_live_config.boundary</c>; the tests drive the EF surface
/// through <see cref="InMemoryUnitLiveConfigStore"/>.
/// </summary>
public class UnitActorBoundaryTests
{
    private static readonly Guid TestUnitGuid = new("aaaaaaaa-0000-0000-0000-00000000bbbb");
    private static readonly string TestUnitActorId = TestUnitGuid.ToString("N");

    private readonly InMemoryUnitLiveConfigStore _liveConfigStore = new();
    private readonly UnitActor _actor;

    public UnitActorBoundaryTests()
    {
        var loggerFactory = Substitute.For<ILoggerFactory>();
        loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var host = ActorHost.CreateForTest<UnitActor>(new ActorTestOptions
        {
            ActorId = new ActorId(TestUnitActorId),
        });
        _actor = new UnitActor(
            host,
            loggerFactory,
            Substitute.For<IRuntimeInvocationPath>(),
            Substitute.For<IActivityEventBus>(),
            Substitute.For<IDirectoryService>(),
            Substitute.For<IActorProxyFactory>(),
            new UnitStateCoordinator(_liveConfigStore, Substitute.For<ILogger<UnitStateCoordinator>>()));
    }

    [Fact]
    public async Task GetBoundaryAsync_NoState_ReturnsEmpty()
    {
        var result = await _actor.GetBoundaryAsync(TestContext.Current.CancellationToken);
        result.ShouldNotBeNull();
        result.IsEmpty.ShouldBeTrue();
    }

    [Fact]
    public async Task SetBoundaryAsync_NonEmpty_PersistsToEf()
    {
        var boundary = new UnitBoundary(
            Opacities: new[] { new BoundaryOpacityRule(DomainPattern: "secret-*") });

        await _actor.SetBoundaryAsync(boundary, TestContext.Current.CancellationToken);

        var fetched = await _liveConfigStore.GetBoundaryAsync(TestUnitGuid, TestContext.Current.CancellationToken);
        fetched.ShouldNotBeNull();
        fetched.Opacities!.Count.ShouldBe(1);
    }

    [Fact]
    public async Task SetBoundaryAsync_Empty_ClearsPersistedBoundary()
    {
        // Seed a non-empty boundary, then write Empty and verify the
        // store reports Empty on the next read (the "no rules" semantics
        // are preserved across the round-trip).
        _liveConfigStore.SeedBoundary(
            TestUnitGuid,
            new UnitBoundary(Opacities: new[] { new BoundaryOpacityRule(DomainPattern: "x") }));

        await _actor.SetBoundaryAsync(UnitBoundary.Empty, TestContext.Current.CancellationToken);

        var fetched = await _liveConfigStore.GetBoundaryAsync(TestUnitGuid, TestContext.Current.CancellationToken);
        fetched.IsEmpty.ShouldBeTrue();
    }
}
