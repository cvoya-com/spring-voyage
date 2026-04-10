// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Routing;

using System.Text.Json;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Routing;
using FluentAssertions;
using global::Dapr.Actors;
using global::Dapr.Actors.Client;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

/// <summary>
/// Unit tests for <see cref="MessageRouter"/>.
/// </summary>
public class MessageRouterTests
{
    private readonly IDirectoryService _directoryService = Substitute.For<IDirectoryService>();
    private readonly IActorProxyFactory _actorProxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly ILoggerFactory _loggerFactory;
    private readonly MessageRouter _router;

    public MessageRouterTests()
    {
        _loggerFactory = Substitute.For<ILoggerFactory>();
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _router = new MessageRouter(_directoryService, _actorProxyFactory, _loggerFactory);
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

        var actorProxy = Substitute.For<IAgentActor>();
        actorProxy.ReceiveAsync(message, Arg.Any<CancellationToken>())
            .Returns(expectedResponse);

        _actorProxyFactory.CreateActorProxy<IAgentActor>(
            Arg.Is<ActorId>(id => id.GetId() == "actor-ada"),
            Arg.Any<string>())
            .Returns(actorProxy);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(expectedResponse);
    }

    [Fact]
    public async Task RouteAsync_direct_uuid_address_resolves_without_directory_lookup()
    {
        var ct = TestContext.Current.CancellationToken;
        var uuid = Guid.NewGuid().ToString();
        var destination = new Address("agent", $"@{uuid}");
        var message = CreateMessage(destination);
        var expectedResponse = CreateResponse(message);

        var actorProxy = Substitute.For<IAgentActor>();
        actorProxy.ReceiveAsync(message, Arg.Any<CancellationToken>())
            .Returns(expectedResponse);

        _actorProxyFactory.CreateActorProxy<IAgentActor>(
            Arg.Is<ActorId>(id => id.GetId() == uuid),
            Arg.Any<string>())
            .Returns(actorProxy);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(expectedResponse);

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

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("ADDRESS_NOT_FOUND");
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

        var proxy1 = Substitute.For<IAgentActor>();
        proxy1.ReceiveAsync(message, Arg.Any<CancellationToken>())
            .Returns(CreateResponse(message, "response-1"));

        var proxy2 = Substitute.For<IAgentActor>();
        proxy2.ReceiveAsync(message, Arg.Any<CancellationToken>())
            .Returns(CreateResponse(message, "response-2"));

        _actorProxyFactory.CreateActorProxy<IAgentActor>(
            Arg.Is<ActorId>(id => id.GetId() == "actor-1"),
            Arg.Any<string>())
            .Returns(proxy1);

        _actorProxyFactory.CreateActorProxy<IAgentActor>(
            Arg.Is<ActorId>(id => id.GetId() == "actor-2"),
            Arg.Any<string>())
            .Returns(proxy2);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().NotBeNull();
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

        _actorProxyFactory.CreateActorProxy<IAgentActor>(
            Arg.Any<ActorId>(),
            Arg.Any<string>())
            .Throws(new InvalidOperationException("Actor unavailable"));

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("DELIVERY_FAILED");
    }

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
