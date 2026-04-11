// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Tools;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.Tools;

using FluentAssertions;

using global::Dapr.Actors.Client;
using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Xunit;

/// <summary>
/// Unit tests for <see cref="RequestHelpTool"/>.
/// </summary>
public class RequestHelpToolTests
{
    private readonly IDirectoryService _directoryService = Substitute.For<IDirectoryService>();
    private readonly IActorProxyFactory _actorProxyFactory = Substitute.For<IActorProxyFactory>();
    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly ToolExecutionContextAccessor _contextAccessor = new();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly RequestHelpTool _tool;

    public RequestHelpToolTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        var permissionService = Substitute.For<IPermissionService>();
        var messageRouter = new MessageRouter(_directoryService, _actorProxyFactory, permissionService, _loggerFactory);
        _tool = new RequestHelpTool(messageRouter, _contextAccessor, _loggerFactory);
        _contextAccessor.Current = new ToolExecutionContext(
            new Address("agent", "test-agent"),
            "conv-1",
            _stateManager);
    }

    [Fact]
    public async Task ExecuteAsync_SendsMessageViaRouter_ReturnsResponse()
    {
        var responsePayload = JsonSerializer.SerializeToElement(new { Answer = "42" });
        var responseMessage = new Message(
            Guid.NewGuid(),
            new Address("agent", "target-agent"),
            new Address("agent", "test-agent"),
            MessageType.Domain,
            "conv-1",
            responsePayload,
            DateTimeOffset.UtcNow);

        // Set up directory to resolve the target address.
        _directoryService.ResolveAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == "target-agent"),
            Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                new Address("agent", "target-agent"),
                "target-actor-id",
                "Target",
                "Target agent",
                null,
                DateTimeOffset.UtcNow));

        // Set up actor proxy to return response.
        var agentProxy = Substitute.For<IAgentActor>();
        agentProxy.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(responseMessage);
        _actorProxyFactory.CreateActorProxy<IAgentActor>(Arg.Any<global::Dapr.Actors.ActorId>(), Arg.Any<string>())
            .Returns(agentProxy);

        var parameters = JsonSerializer.SerializeToElement(new
        {
            targetScheme = "agent",
            targetPath = "target-agent",
            message = "Help me with this"
        });

        var result = await _tool.ExecuteAsync(
            parameters,
            JsonSerializer.SerializeToElement(new { }),
            TestContext.Current.CancellationToken);

        result.GetProperty("Success").GetBoolean().Should().BeTrue();
        result.GetProperty("Response").GetProperty("Answer").GetString().Should().Be("42");
    }
}