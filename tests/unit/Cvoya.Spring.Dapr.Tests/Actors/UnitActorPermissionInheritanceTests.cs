// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Actors;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
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
/// Tests for <see cref="UnitActor.GetPermissionInheritanceAsync"/> and
/// <see cref="UnitActor.SetPermissionInheritanceAsync"/> (#414). Per
/// ADR-0040 / #2049 the inheritance flag lives on
/// <c>unit_live_config.permission_inheritance</c>; the tests drive the
/// EF surface through <see cref="InMemoryUnitLiveConfigStore"/>.
/// </summary>
public class UnitActorPermissionInheritanceTests
{
    private static readonly Guid TestUnitGuid = new("aaaaaaaa-0000-0000-0000-00000000cccc");
    private static readonly string TestUnitActorId = TestUnitGuid.ToString("N");

    private readonly InMemoryUnitLiveConfigStore _liveConfigStore = new();
    private readonly UnitActor _actor;

    public UnitActorPermissionInheritanceTests()
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
            new UnitStateCoordinator(_liveConfigStore, Substitute.For<ILogger<UnitStateCoordinator>>()),
            new InMemoryUnitMemberGraphStore());
    }

    [Fact]
    public async Task GetPermissionInheritanceAsync_NoState_ReturnsInherit()
    {
        var result = await _actor.GetPermissionInheritanceAsync(TestContext.Current.CancellationToken);

        result.ShouldBe(UnitPermissionInheritance.Inherit);
    }

    [Fact]
    public async Task SetPermissionInheritanceAsync_Isolated_PersistsToEf()
    {
        await _actor.SetPermissionInheritanceAsync(
            UnitPermissionInheritance.Isolated,
            TestContext.Current.CancellationToken);

        var fetched = await _liveConfigStore.GetPermissionInheritanceAsync(
            TestUnitGuid, TestContext.Current.CancellationToken);
        fetched.ShouldBe(UnitPermissionInheritance.Isolated);
    }

    [Fact]
    public async Task SetPermissionInheritanceAsync_Inherit_PersistsInherit()
    {
        // Per ADR-0040 / #2049 the row is materialised on every set so
        // the inheritance walk has a single SQL read regardless of the
        // operator's choice. The pre-#2049 "Inherit removes the row"
        // optimisation no longer applies — Inherit is now an explicit
        // value on the row.
        _liveConfigStore.SeedInheritance(TestUnitGuid, UnitPermissionInheritance.Isolated);

        await _actor.SetPermissionInheritanceAsync(
            UnitPermissionInheritance.Inherit,
            TestContext.Current.CancellationToken);

        var fetched = await _liveConfigStore.GetPermissionInheritanceAsync(
            TestUnitGuid, TestContext.Current.CancellationToken);
        fetched.ShouldBe(UnitPermissionInheritance.Inherit);
    }
}
