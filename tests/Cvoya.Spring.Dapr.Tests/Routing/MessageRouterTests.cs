// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Routing;

using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Identifiers;
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
///
/// Post #1629: every Address path is a no-dash 32-hex Guid; tests use named
/// Guid constants for the actor identities.
/// </summary>
public class MessageRouterTests
{
    private static readonly Guid AdaId = new("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid BobId = new("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid SenderId = new("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly Guid UnitOneId = new("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid HumanLocalId = new("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid HumanAuthorisedId = new("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid HumanUnauthorisedId = new("cccccccc-0000-0000-0000-000000000003");

    private static string Hex(Guid g) => g.ToString("N");

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
        var destination = new Address("agent", AdaId);
        var entry = new DirectoryEntry(destination, AdaId, "Ada", "Engineer", null, DateTimeOffset.UtcNow);
        var message = CreateMessage(destination);
        var expectedResponse = CreateResponse(message);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>())
            .Returns(entry);

        var actorProxy = Substitute.For<IAgent>();
        actorProxy.ReceiveAsync(message, Arg.Any<CancellationToken>())
            .Returns(expectedResponse);

        _agentProxyResolver.Resolve("agent", Hex(AdaId)).Returns(actorProxy);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(expectedResponse);
    }

    [Fact]
    public async Task RouteAsync_unknown_address_returns_AddressNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("agent", Guid.NewGuid());
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
        var roleAddress = new Address("role", Guid.NewGuid()); // Role address: scheme triggers multicast.
        var message = CreateMessage(roleAddress);

        var entry1 = new DirectoryEntry(
            new Address("agent", AdaId), AdaId, "Ada", "Engineer", "backend-engineer", DateTimeOffset.UtcNow);
        var entry2 = new DirectoryEntry(
            new Address("agent", BobId), BobId, "Bob", "Engineer", "backend-engineer", DateTimeOffset.UtcNow);

        _directoryService.ResolveByRoleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([entry1, entry2]);

        var proxy1 = Substitute.For<IAgent>();
        proxy1.ReceiveAsync(message, Arg.Any<CancellationToken>())
            .Returns(CreateResponse(message, "response-1"));

        var proxy2 = Substitute.For<IAgent>();
        proxy2.ReceiveAsync(message, Arg.Any<CancellationToken>())
            .Returns(CreateResponse(message, "response-2"));

        _agentProxyResolver.Resolve("agent", Hex(AdaId)).Returns(proxy1);
        _agentProxyResolver.Resolve("agent", Hex(BobId)).Returns(proxy2);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldNotBeNull();
    }

    [Fact]
    public async Task RouteAsync_delivery_failure_returns_DeliveryFailed()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("agent", AdaId);
        var entry = new DirectoryEntry(destination, AdaId, "Ada", "Engineer", null, DateTimeOffset.UtcNow);
        var message = CreateMessage(destination);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>())
            .Returns(entry);

        var actorProxy = Substitute.For<IAgent>();
        actorProxy.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Actor unavailable"));

        _agentProxyResolver.Resolve("agent", Hex(AdaId)).Returns(actorProxy);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("DELIVERY_FAILED");
    }

    [Fact]
    public async Task RouteAsync_caller_validation_exception_returns_CallerValidation()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("agent", AdaId);
        var entry = new DirectoryEntry(destination, AdaId, "Ada", "Engineer", null, DateTimeOffset.UtcNow);
        var message = CreateMessage(destination);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>())
            .Returns(entry);

        var actorProxy = Substitute.For<IAgent>();
        actorProxy.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Throws(new CallerValidationException(
                CallerValidationCodes.MissingThreadId,
                "Domain messages must have a ThreadId"));

        _agentProxyResolver.Resolve("agent", Hex(AdaId)).Returns(actorProxy);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("CALLER_VALIDATION");
        result.Error.DetailCode.ShouldBe(CallerValidationCodes.MissingThreadId);
        result.Error.Detail.ShouldBe("Domain messages must have a ThreadId");
    }

    [Fact]
    public async Task RouteAsync_caller_validation_encoded_in_message_survives_remoting()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("agent", AdaId);
        var entry = new DirectoryEntry(destination, AdaId, "Ada", "Engineer", null, DateTimeOffset.UtcNow);
        var message = CreateMessage(destination);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>())
            .Returns(entry);

        var encodedMessage = new CallerValidationException(
            CallerValidationCodes.UnknownMessageType,
            "Unknown message type: Amendment").Message;

        var actorProxy = Substitute.For<IAgent>();
        actorProxy.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException(encodedMessage));

        _agentProxyResolver.Resolve("agent", Hex(AdaId)).Returns(actorProxy);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("CALLER_VALIDATION");
        result.Error.DetailCode.ShouldBe(CallerValidationCodes.UnknownMessageType);
        result.Error.Detail.ShouldBe("Unknown message type: Amendment");
    }

    // --- Permission Check Tests ---

    [Fact]
    public async Task RouteAsync_HumanToUnitWithPermission_Succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("unit", UnitOneId);
        var entry = new DirectoryEntry(destination, UnitOneId, "Engineering", "Team", null, DateTimeOffset.UtcNow);
        var message = CreateMessageFromHuman(destination, HumanAuthorisedId);
        var expectedResponse = CreateResponse(message);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>()).Returns(entry);
        _permissionService.ResolveEffectivePermissionAsync(Hex(HumanAuthorisedId), Hex(UnitOneId), Arg.Any<CancellationToken>())
            .Returns(PermissionLevel.Operator);

        var unitProxy = Substitute.For<IAgent>();
        unitProxy.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>()).Returns(expectedResponse);

        _agentProxyResolver.Resolve("unit", Hex(UnitOneId)).Returns(unitProxy);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task RouteAsync_HumanToUnitWithoutPermission_ReturnsPermissionDenied()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("unit", UnitOneId);
        var entry = new DirectoryEntry(destination, UnitOneId, "Engineering", "Team", null, DateTimeOffset.UtcNow);
        var message = CreateMessageFromHuman(destination, HumanUnauthorisedId);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>()).Returns(entry);
        _permissionService.ResolveEffectivePermissionAsync(Hex(HumanUnauthorisedId), Hex(UnitOneId), Arg.Any<CancellationToken>())
            .Returns((PermissionLevel?)null);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("PERMISSION_DENIED");
    }

    // #1037: human:// addresses must resolve directly to their actor id
    // without a directory lookup.
    [Fact]
    public async Task RouteAsync_HumanDestination_BypassesDirectoryAndDelivers()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("human", HumanLocalId);
        var message = CreateMessage(destination);
        var expectedResponse = CreateResponse(message);

        // Explicitly: no directory entry registered for the human address.
        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>())
            .Returns((DirectoryEntry?)null);

        var humanProxy = Substitute.For<IAgent>();
        humanProxy.ReceiveAsync(message, Arg.Any<CancellationToken>())
            .Returns(expectedResponse);

        _agentProxyResolver.Resolve("human", Hex(HumanLocalId)).Returns(humanProxy);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(expectedResponse);

        // Directory service should NOT have been consulted for human addresses.
        await _directoryService.DidNotReceive().ResolveAsync(Arg.Any<Address>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RouteAsync_AgentToUnit_SkipsPermissionCheck()
    {
        var ct = TestContext.Current.CancellationToken;
        var destination = new Address("unit", UnitOneId);
        var entry = new DirectoryEntry(destination, UnitOneId, "Engineering", "Team", null, DateTimeOffset.UtcNow);
        var message = CreateMessage(destination); // From agent, not human
        var expectedResponse = CreateResponse(message);

        _directoryService.ResolveAsync(destination, Arg.Any<CancellationToken>()).Returns(entry);

        var unitProxy = Substitute.For<IAgent>();
        unitProxy.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>()).Returns(expectedResponse);

        _agentProxyResolver.Resolve("unit", Hex(UnitOneId)).Returns(unitProxy);

        var result = await _router.RouteAsync(message, ct);

        result.IsSuccess.ShouldBeTrue();
        // Permission service should NOT have been called for agent-to-unit routing.
        await _permissionService.DidNotReceive().ResolveEffectivePermissionAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static Message CreateMessageFromHuman(Address to, Guid humanId) =>
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
            new Address("agent", SenderId),
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
            original.ThreadId,
            JsonSerializer.SerializeToElement(new { Acknowledged = true, Label = label }),
            DateTimeOffset.UtcNow);
}
