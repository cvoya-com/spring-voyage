// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Routing;

using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Routing;

using Microsoft.Extensions.Logging;

using NSubstitute;
using NSubstitute.ExceptionExtensions;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="MessageRouter"/>.
/// </summary>
public class MessageRouterTests
{
    private readonly IDirectoryService _directoryService = Substitute.For<IDirectoryService>();
    private readonly IAgentProxyResolver _agentProxyResolver = Substitute.For<IAgentProxyResolver>();
    private readonly IPermissionService _permissionService = Substitute.For<IPermissionService>();
    private readonly ILoggerFactory _loggerFactory;
    private readonly MessageRouter _router;

    public MessageRouterTests()
    {
        _loggerFactory = Substitute.For<ILoggerFactory>();
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _router = new MessageRouter(_directoryService, _agentProxyResolver, _permissionService, _loggerFactory);
    }

    [Fact]
    public async Task RouteAsync_path_address_resolves_and_delivers_message()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("agent", "engineering-team/ada");
        var entry = new DirectoryEntry(destination, "actor-ada", "Ada", "Engineer", null, DateTimeOffset.UtcNow);
        var message = CreateMessage(destination);
        var expectedResponse = CreateResponse(message);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>())
            .Returns(entry);

        var actorProxy = Substitute.For<IAgent>();
        actorProxy.ReceiveAsync(message, Arg.Any<CancellationToken>())
            .Returns(expectedResponse);

        _agentProxyResolver.Resolve("agent", "actor-ada").Returns(actorProxy);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(expectedResponse);
    }

    [Fact]
    public async Task RouteAsync_direct_uuid_address_resolves_without_directory_lookup()
    {
        var ct = TestContext.Current.CancellationToken;
        var uuid = Guid.NewGuid().ToString();
        var destination = new Address("agent", $"@{uuid}");
        var message = CreateMessage(destination);
        var expectedResponse = CreateResponse(message);

        var actorProxy = Substitute.For<IAgent>();
        actorProxy.ReceiveAsync(message, Arg.Any<CancellationToken>())
            .Returns(expectedResponse);

        _agentProxyResolver.Resolve("agent", uuid).Returns(actorProxy);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(expectedResponse);

        // Directory service should NOT have been called.
        await _directoryService.DidNotReceive().ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RouteAsync_unknown_address_returns_AddressNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("agent", "nonexistent/agent");
        var message = CreateMessage(destination);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("ADDRESS_NOT_FOUND");
    }

    [Fact]
    public async Task RouteAsync_multicast_role_address_fans_out_to_multiple_actors()
    {
        var ct = TestContext.Current.CancellationToken;
        var roleAddress = new Address("role", "backend-engineer");
        var message = CreateMessage(roleAddress);

        var entry1 = new DirectoryEntry(
            new Address("agent", "team/ada"), "actor-1", "Ada", "Engineer", "backend-engineer", DateTimeOffset.UtcNow);
        var entry2 = new DirectoryEntry(
            new Address("agent", "team/bob"), "actor-2", "Bob", "Engineer", "backend-engineer", DateTimeOffset.UtcNow);

        _directoryService.ResolveByRoleAsync("backend-engineer", Arg.Any<CancellationToken>())
            .Returns([entry1, entry2]);

        var proxy1 = Substitute.For<IAgent>();
        proxy1.ReceiveAsync(message, Arg.Any<CancellationToken>())
            .Returns(CreateResponse(message, "response-1"));

        var proxy2 = Substitute.For<IAgent>();
        proxy2.ReceiveAsync(message, Arg.Any<CancellationToken>())
            .Returns(CreateResponse(message, "response-2"));

        _agentProxyResolver.Resolve("agent", "actor-1").Returns(proxy1);
        _agentProxyResolver.Resolve("agent", "actor-2").Returns(proxy2);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
    }

    [Fact]
    public async Task RouteAsync_delivery_failure_returns_DeliveryFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("agent", "engineering-team/ada");
        var entry = new DirectoryEntry(destination, "actor-ada", "Ada", "Engineer", null, DateTimeOffset.UtcNow);
        var message = CreateMessage(destination);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>())
            .Returns(entry);

        var actorProxy = Substitute.For<IAgent>();
        actorProxy.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Actor unavailable"));

        _agentProxyResolver.Resolve("agent", "actor-ada").Returns(actorProxy);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("DELIVERY_FAILED");
    }

    // --- Permission Check Tests ---

    [Fact]
    public async Task RouteAsync_HumanToUnitWithPermission_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("unit", "engineering-team");
        var entry = new DirectoryEntry(destination, "unit-1", "Engineering", "Team", null, DateTimeOffset.UtcNow);
        var message = CreateMessageFromHuman(destination, "human-1");
        var expectedResponse = CreateResponse(message);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>()).Returns(entry);
        _permissionService.ResolvePermissionAsync("human-1", "unit-1", Arg.Any<CancellationToken>())
            .Returns(PermissionLevel.Operator);

        var unitProxy = Substitute.For<IAgent>();
        unitProxy.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>()).Returns(expectedResponse);

        _agentProxyResolver.Resolve("unit", "unit-1").Returns(unitProxy);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task RouteAsync_HumanToUnitWithoutPermission_ReturnsPermissionDenied()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("unit", "engineering-team");
        var entry = new DirectoryEntry(destination, "unit-1", "Engineering", "Team", null, DateTimeOffset.UtcNow);
        var message = CreateMessageFromHuman(destination, "unauthorized-human");

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>()).Returns(entry);
        _permissionService.ResolvePermissionAsync("unauthorized-human", "unit-1", Arg.Any<CancellationToken>())
            .Returns((PermissionLevel?)null);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("PERMISSION_DENIED");
    }

    [Fact]
    public async Task RouteAsync_AgentToUnit_SkipsPermissionCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("unit", "engineering-team");
        var entry = new DirectoryEntry(destination, "unit-1", "Engineering", "Team", null, DateTimeOffset.UtcNow);
        var message = CreateMessage(destination); // From agent, not human
        var expectedResponse = CreateResponse(message);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>()).Returns(entry);

        var unitProxy = Substitute.For<IAgent>();
        unitProxy.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>()).Returns(expectedResponse);

        _agentProxyResolver.Resolve("unit", "unit-1").Returns(unitProxy);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeTrue();
        // Permission service should NOT have been called for agent-to-unit routing.
        await _permissionService.DidNotReceive().ResolvePermissionAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static Message CreateMessageFromHuman(Address to, string humanId) =>
        new(
            Guid.NewGuid(),
            new Address("human", humanId),
            to,
            MessageType.Domain,
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Content = "hello" }),
            DateTimeOffset.UtcNow);

    private static Message CreateMessage(Address to) =>
        new(
            Guid.NewGuid(),
            new Address("agent", "test-sender"),
            to,
            MessageType.Domain,
            Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Content = "hello" }),
            DateTimeOffset.UtcNow);

    private static Message CreateResponse(Message original, string? label = null) =>
        new(
            Guid.NewGuid(),
            original.To,
            original.From,
            MessageType.Domain,
            original.ConversationId,
            JsonSerializer.SerializeToElement(new { Acknowledged = true, Label = label }),
            DateTimeOffset.UtcNow);
}