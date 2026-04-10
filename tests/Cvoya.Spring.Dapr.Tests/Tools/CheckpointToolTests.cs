// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Tools;

using System.Text.Json;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Tools;
using FluentAssertions;
using global::Dapr.Actors.Runtime;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

/// <summary>
/// Unit tests for <see cref="CheckpointTool"/>.
/// </summary>
public class CheckpointToolTests
{
    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly ToolExecutionContextAccessor _contextAccessor = new();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly CheckpointTool _tool;

    public CheckpointToolTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _tool = new CheckpointTool(_contextAccessor, _loggerFactory);
        _contextAccessor.Current = new ToolExecutionContext(
            new Address("agent", "test-agent"),
            "conv-1",
            _stateManager);
    }

    [Fact]
    public async Task ExecuteAsync_StoresCheckpointData()
    {
        var checkpointData = JsonSerializer.SerializeToElement(new { Step = 3, Progress = "halfway" });
        var parameters = JsonSerializer.SerializeToElement(new { data = checkpointData });

        var result = await _tool.ExecuteAsync(
            parameters,
            JsonSerializer.SerializeToElement(new { }),
            TestContext.Current.CancellationToken);

        result.GetProperty("Success").GetBoolean().Should().BeTrue();

        await _stateManager.Received(1).SetStateAsync(
            "Agent:Checkpoint:conv-1",
            Arg.Any<JsonElement>(),
            Arg.Any<CancellationToken>());
    }
}
