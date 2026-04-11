// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Auth;

using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;

using FluentAssertions;

using global::Dapr.Actors;
using global::Dapr.Actors.Client;

using Microsoft.Extensions.Logging;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Xunit;

/// <summary>
/// Unit tests for <see cref="PermissionService"/>.
/// </summary>
public class PermissionServiceTests
{
    private readonly IActorProxyFactory _actorProxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly PermissionService _service;

    public PermissionServiceTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _service = new PermissionService(_actorProxyFactory, _loggerFactory);
    }

    [Fact]
    public async Task ResolvePermissionAsync_UnitHasPermission_ReturnsPermissionLevel()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitProxy = Substitute.For<IUnitActor>();
        unitProxy.GetHumanPermissionAsync("human-1", ct).Returns(PermissionLevel.Operator);

        _actorProxyFactory.CreateActorProxy<IUnitActor>(
            Arg.Is<ActorId>(id => id.GetId() == "unit-1"),
            nameof(IUnitActor)).Returns(unitProxy);

        var result = await _service.ResolvePermissionAsync("human-1", "unit-1", ct);

        result.Should().Be(PermissionLevel.Operator);
    }

    [Fact]
    public async Task ResolvePermissionAsync_UnitHasNoPermission_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitProxy = Substitute.For<IUnitActor>();
        unitProxy.GetHumanPermissionAsync("human-1", ct).Returns((PermissionLevel?)null);

        _actorProxyFactory.CreateActorProxy<IUnitActor>(
            Arg.Is<ActorId>(id => id.GetId() == "unit-1"),
            nameof(IUnitActor)).Returns(unitProxy);

        var result = await _service.ResolvePermissionAsync("human-1", "unit-1", ct);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolvePermissionAsync_ActorThrowsException_ReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;
        var unitProxy = Substitute.For<IUnitActor>();
        unitProxy.GetHumanPermissionAsync("human-1", ct)
            .ThrowsAsync(new InvalidOperationException("Actor unavailable"));

        _actorProxyFactory.CreateActorProxy<IUnitActor>(
            Arg.Is<ActorId>(id => id.GetId() == "unit-1"),
            nameof(IUnitActor)).Returns(unitProxy);

        var result = await _service.ResolvePermissionAsync("human-1", "unit-1", ct);

        result.Should().BeNull();
    }
}