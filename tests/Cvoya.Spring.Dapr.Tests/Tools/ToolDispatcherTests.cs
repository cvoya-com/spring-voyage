// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Tools;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tools;
using Cvoya.Spring.Dapr.Tools;

using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ToolDispatcher"/>.
/// </summary>
public class ToolDispatcherTests
{
    private readonly PlatformToolRegistry _registry = new();
    private readonly ToolExecutionContextAccessor _contextAccessor = new();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly ToolDispatcher _dispatcher;

    public ToolDispatcherTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _dispatcher = new ToolDispatcher(_registry, _contextAccessor, _loggerFactory);
    }

    [Fact]
    public async Task DispatchAsync_KnownTool_DispatchesToCorrectTool()
    {
        var expectedResult = JsonSerializer.SerializeToElement(new { Result = "ok" });
        var tool = Substitute.For<IPlatformTool>();
        tool.Name.Returns("myTool");
        tool.ExecuteAsync(Arg.Any<JsonElement>(), Arg.Any<JsonElement>(), Arg.Any<CancellationToken>())
            .Returns(expectedResult);
        _registry.Register(tool);

        var stateManager = Substitute.For<IActorStateManager>();
        var executionContext = new ToolExecutionContext(
            Address.For("agent", TestSlugIds.HexFor("test-agent")),
            "conv-1",
            stateManager);

        var result = await _dispatcher.DispatchAsync(
            "myTool",
            JsonSerializer.SerializeToElement(new { }),
            executionContext,
            TestContext.Current.CancellationToken);

        result.GetProperty("Result").GetString().ShouldBe("ok");
        await tool.Received(1).ExecuteAsync(
            Arg.Any<JsonElement>(),
            Arg.Any<JsonElement>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_UnknownTool_ThrowsSpringException()
    {
        var stateManager = Substitute.For<IActorStateManager>();
        var executionContext = new ToolExecutionContext(
            Address.For("agent", TestSlugIds.HexFor("test-agent")),
            "conv-1",
            stateManager);

        var act = async () => await _dispatcher.DispatchAsync(
            "unknownTool",
            JsonSerializer.SerializeToElement(new { }),
            executionContext,
            TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<SpringException>(act);
        ex.Message.ShouldBe("Unknown tool: unknownTool");
    }
}