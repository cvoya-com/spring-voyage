// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Orchestration;

using System.Text.Json;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Orchestration;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

/// <summary>
/// Unit tests for <see cref="HybridOrchestrationStrategy"/>.
/// </summary>
public class HybridOrchestrationStrategyTests
{
    private readonly IAiProvider _aiProvider = Substitute.For<IAiProvider>();
    private readonly IContainerRuntime _containerRuntime = Substitute.For<IContainerRuntime>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IUnitContext _context = Substitute.For<IUnitContext>();
    private readonly HybridOrchestrationStrategy _strategy;
    private readonly WorkflowOrchestrationOptions _options;

    public HybridOrchestrationStrategyTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        _options = new WorkflowOrchestrationOptions
        {
            ContainerImage = "spring-hybrid:latest",
            Timeout = TimeSpan.FromMinutes(15)
        };

        var lifecycleManager = new ContainerLifecycleManager(
            _containerRuntime,
            Substitute.For<IDaprSidecarManager>(),
            Options.Create(new ContainerRuntimeOptions()),
            _loggerFactory);

        _strategy = new HybridOrchestrationStrategy(
            _aiProvider,
            _containerRuntime,
            lifecycleManager,
            Options.Create(_options),
            _loggerFactory);

        _context.UnitAddress.Returns(new Address("unit", "test-unit"));
        _context.Members.Returns([new Address("agent", "agent-1")]);
    }

    private static Message CreateMessage(string? conversationId = null)
    {
        return new Message(
            Guid.NewGuid(),
            new Address("agent", "test-sender"),
            new Address("unit", "test-unit"),
            MessageType.Domain,
            conversationId ?? Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Task = "process request" }),
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task OrchestrateAsync_UsesAiForClassificationThenContainerForExecution()
    {
        var message = CreateMessage();
        _aiProvider.CompleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("process");
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("container-1", 0, "hybrid result", ""));

        var result = await _strategy.OrchestrateAsync(message, _context, TestContext.Current.CancellationToken);

        // Verify AI was called for classification
        await _aiProvider.Received(1).CompleteAsync(
            Arg.Is<string>(p => p.Contains("classifier")),
            Arg.Any<CancellationToken>());

        // Verify container was called with classification
        await _containerRuntime.Received(1).RunAsync(
            Arg.Is<ContainerConfig>(c =>
                c.Image == "spring-hybrid:latest" &&
                c.EnvironmentVariables != null &&
                c.EnvironmentVariables.ContainsKey("SPRING_CLASSIFICATION") &&
                c.EnvironmentVariables["SPRING_CLASSIFICATION"] == "process"),
            Arg.Any<CancellationToken>());

        result.Should().NotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Output").GetString().Should().Be("hybrid result");
    }

    [Fact]
    public async Task OrchestrateAsync_ReturnsNull_WhenAiClassificationFails()
    {
        var message = CreateMessage();
        _aiProvider.CompleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("AI service unavailable"));

        var result = await _strategy.OrchestrateAsync(message, _context, TestContext.Current.CancellationToken);

        result.Should().BeNull();
        await _containerRuntime.DidNotReceive().RunAsync(
            Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrchestrateAsync_ReturnsNull_WhenContainerFails()
    {
        var message = CreateMessage();
        _aiProvider.CompleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("escalate");
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("container-1", 1, "", "container error"));

        var result = await _strategy.OrchestrateAsync(message, _context, TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [Fact]
    public void BuildClassificationPrompt_IncludesMessageAndMembers()
    {
        var message = CreateMessage();
        var prompt = HybridOrchestrationStrategy.BuildClassificationPrompt(message, _context);

        prompt.Should().Contain("classifier");
        prompt.Should().Contain("agent://agent-1");
        prompt.Should().Contain("process request");
    }
}
