// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Orchestration;

using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Orchestration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="WorkflowOrchestrationStrategy"/>.
/// </summary>
public class WorkflowOrchestrationStrategyTests
{
    private readonly IContainerRuntime _containerRuntime = Substitute.For<IContainerRuntime>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IUnitContext _context = Substitute.For<IUnitContext>();
    private readonly WorkflowOrchestrationStrategy _strategy;
    private readonly WorkflowOrchestrationOptions _options;

    public WorkflowOrchestrationStrategyTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        _options = new WorkflowOrchestrationOptions
        {
            ContainerImage = "spring-workflow:latest",
            Timeout = TimeSpan.FromMinutes(10)
        };

        var lifecycleManager = new ContainerLifecycleManager(
            _containerRuntime,
            Substitute.For<IDaprSidecarManager>(),
            Options.Create(new DaprSidecarOptions()),
            _loggerFactory);

        _strategy = new WorkflowOrchestrationStrategy(
            _containerRuntime,
            lifecycleManager,
            Options.Create(_options),
            _loggerFactory);

        _context.UnitAddress.Returns(Address.For("unit", TestSlugIds.HexFor("test-unit")));
        _context.Members.Returns([Address.For("agent", TestSlugIds.HexFor("agent-1"))]);
    }

    private static Message CreateMessage(string? threadId = null)
    {
        return new Message(
            Guid.NewGuid(),
            Address.For("agent", TestSlugIds.HexFor("test-sender")),
            Address.For("unit", TestSlugIds.HexFor("test-unit")),
            MessageType.Domain,
            threadId ?? Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Task = "run workflow" }),
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task OrchestrateAsync_LaunchesContainerWithCorrectConfig()
    {
        var message = CreateMessage();
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("container-1", 0, "workflow output", ""));

        await _strategy.OrchestrateAsync(message, _context, TestContext.Current.CancellationToken);

        await _containerRuntime.Received(1).RunAsync(
            Arg.Is<ContainerConfig>(c =>
                c.Image == "spring-workflow:latest" &&
                c.EnvironmentVariables != null &&
                c.EnvironmentVariables.ContainsKey("SPRING_MESSAGE") &&
                c.EnvironmentVariables.ContainsKey("SPRING_MEMBERS") &&
                c.Timeout == TimeSpan.FromMinutes(10)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrchestrateAsync_ReturnsContainerOutputAsMessage()
    {
        var message = CreateMessage();
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("container-1", 0, "workflow result data", ""));

        var result = await _strategy.OrchestrateAsync(message, _context, TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result!.Type.ShouldBe(MessageType.Domain);
        result.From.ShouldBe(message.To);
        result.To.ShouldBe(message.From);
        result.ThreadId.ShouldBe(message.ThreadId);

        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Output").GetString().ShouldBe("workflow result data");
    }

    [Fact]
    public async Task OrchestrateAsync_ReturnsNull_WhenContainerFails()
    {
        var message = CreateMessage();
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("container-1", 1, "", "error occurred"));

        var result = await _strategy.OrchestrateAsync(message, _context, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }

    [Fact]
    public async Task OrchestrateAsync_ReturnsNull_WhenContainerOutputIsEmpty()
    {
        var message = CreateMessage();
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("container-1", 0, "", ""));

        var result = await _strategy.OrchestrateAsync(message, _context, TestContext.Current.CancellationToken);

        result.ShouldBeNull();
    }
}