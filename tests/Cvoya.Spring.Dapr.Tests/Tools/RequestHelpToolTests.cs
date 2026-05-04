// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Tools;

using System.Text.Json;

using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.Tools;

using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="RequestHelpTool"/>.
/// </summary>
public class RequestHelpToolTests
{
    private readonly IDirectoryService _directoryService = Substitute.For<IDirectoryService>();
    private readonly IAgentProxyResolver _agentProxyResolver = Substitute.For<IAgentProxyResolver>();
    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly ToolExecutionContextAccessor _contextAccessor = new();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly RequestHelpTool _tool;

    public RequestHelpToolTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        var permissionService = Substitute.For<IPermissionService>();
        var messageRouter = new MessageRouter(_directoryService, _agentProxyResolver, permissionService, _loggerFactory);
        _tool = new RequestHelpTool(messageRouter, _contextAccessor, _loggerFactory);
        _contextAccessor.Current = new ToolExecutionContext(
            Address.For("agent", TestSlugIds.HexFor("test-agent")),
            "conv-1",
            _stateManager);
    }

    [Fact]
    public async Task ExecuteAsync_SendsMessageViaRouter_ReturnsResponse()
    {
        var responsePayload = JsonSerializer.SerializeToElement(new { Answer = "42" });
        var responseMessage = new Message(
            Guid.NewGuid(),
            Address.For("agent", TestSlugIds.HexFor("target-agent")),
            Address.For("agent", TestSlugIds.HexFor("test-agent")),
            MessageType.Domain,
            "conv-1",
            responsePayload,
            DateTimeOffset.UtcNow);

        // Set up directory to resolve the target address.
        var targetActorId = TestSlugIds.For("target-agent");
        _directoryService.ResolveAsync(
            Arg.Is<Address>(a => a.Scheme == "agent" && a.Path == TestSlugIds.HexFor("target-agent")),
            Arg.Any<CancellationToken>())
            .Returns(new DirectoryEntry(
                Address.For("agent", TestSlugIds.HexFor("target-agent")),
                targetActorId,
                "Target",
                "Target agent",
                null,
                DateTimeOffset.UtcNow));

        // Set up actor proxy to return response.
        var agentProxy = Substitute.For<IAgent>();
        agentProxy.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(responseMessage);
        _agentProxyResolver.Resolve("agent", Arg.Any<string>()).Returns(agentProxy);

        var parameters = JsonSerializer.SerializeToElement(new
        {
            targetScheme = "agent",
            targetPath = TestSlugIds.HexFor("target-agent"),
            message = "Help me with this"
        });

        var result = await _tool.ExecuteAsync(
            parameters,
            JsonSerializer.SerializeToElement(new { }),
            TestContext.Current.CancellationToken);

        result.GetProperty("Success").GetBoolean().ShouldBeTrue();
        result.GetProperty("Response").GetProperty("Answer").GetString().ShouldBe("42");
    }
}