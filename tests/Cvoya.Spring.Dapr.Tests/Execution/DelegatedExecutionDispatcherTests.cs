// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Text.Json;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Execution;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

/// <summary>
/// Unit tests for <see cref="DelegatedExecutionDispatcher"/>.
/// </summary>
public class DelegatedExecutionDispatcherTests
{
    private readonly IContainerRuntime _containerRuntime = Substitute.For<IContainerRuntime>();
    private readonly IPromptAssembler _promptAssembler = Substitute.For<IPromptAssembler>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly DelegatedExecutionDispatcher _dispatcher;

    public DelegatedExecutionDispatcherTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _dispatcher = new DelegatedExecutionDispatcher(_containerRuntime, _promptAssembler, _loggerFactory);
    }

    private static Message CreateMessage(
        string toPath = "test-image:latest",
        string? conversationId = null)
    {
        return new Message(
            Guid.NewGuid(),
            new Address("agent", "sender"),
            new Address("agent", toPath),
            MessageType.Domain,
            conversationId ?? Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { Task = "do-work" }),
            DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task DispatchAsync_DelegatedMode_CallsContainerRuntime()
    {
        var message = CreateMessage();
        var prompt = "assembled prompt";

        _promptAssembler.AssembleAsync(message, Arg.Any<CancellationToken>())
            .Returns(prompt);
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("spring-exec-123", 0, "output", ""));

        await _dispatcher.DispatchAsync(message, ExecutionMode.Delegated, TestContext.Current.CancellationToken);

        await _containerRuntime.Received(1).RunAsync(
            Arg.Any<ContainerConfig>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchAsync_HostedMode_ThrowsSpringException()
    {
        var message = CreateMessage();

        var act = () => _dispatcher.DispatchAsync(
            message, ExecutionMode.Hosted, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<SpringException>()
            .WithMessage("*Delegated*Hosted*");
    }

    [Fact]
    public async Task DispatchAsync_ContainerSucceeds_ReturnsResponseMessage()
    {
        var message = CreateMessage();
        var prompt = "test prompt";

        _promptAssembler.AssembleAsync(message, Arg.Any<CancellationToken>())
            .Returns(prompt);
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("spring-exec-456", 0, "success output", ""));

        var result = await _dispatcher.DispatchAsync(
            message, ExecutionMode.Delegated, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.From.Should().Be(message.To);
        result.To.Should().Be(message.From);
        result.ConversationId.Should().Be(message.ConversationId);
        result.Type.Should().Be(MessageType.Domain);

        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("Output").GetString().Should().Be("success output");
        payload.GetProperty("ExitCode").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task DispatchAsync_ContainerFails_ReturnsErrorMessage()
    {
        var message = CreateMessage();

        _promptAssembler.AssembleAsync(message, Arg.Any<CancellationToken>())
            .Returns("prompt");
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("spring-exec-789", 1, "", "error occurred"));

        var result = await _dispatcher.DispatchAsync(
            message, ExecutionMode.Delegated, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        var payload = result!.Payload.Deserialize<JsonElement>();
        payload.GetProperty("ExitCode").GetInt32().Should().Be(1);
        payload.GetProperty("Error").GetString().Should().Be("error occurred");
    }

    [Fact]
    public async Task DispatchAsync_CancellationRequested_StopsContainer()
    {
        var message = CreateMessage();
        using var cts = new CancellationTokenSource();

        _promptAssembler.AssembleAsync(message, Arg.Any<CancellationToken>())
            .Returns("prompt");
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var ct = callInfo.ArgAt<CancellationToken>(1);
                cts.Cancel();
                ct.ThrowIfCancellationRequested();
                return new ContainerResult("spring-exec-cancel", 0, "", "");
            });

        var act = () => _dispatcher.DispatchAsync(message, ExecutionMode.Delegated, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DispatchAsync_PassesPromptAsEnvironmentVariable()
    {
        var message = CreateMessage();
        var expectedPrompt = "the assembled prompt";

        _promptAssembler.AssembleAsync(message, Arg.Any<CancellationToken>())
            .Returns(expectedPrompt);
        _containerRuntime.RunAsync(Arg.Any<ContainerConfig>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerResult("spring-exec-env", 0, "output", ""));

        await _dispatcher.DispatchAsync(
            message, ExecutionMode.Delegated, TestContext.Current.CancellationToken);

        await _containerRuntime.Received(1).RunAsync(
            Arg.Is<ContainerConfig>(c =>
                c.EnvironmentVariables != null &&
                c.EnvironmentVariables.ContainsKey("SPRING_SYSTEM_PROMPT") &&
                c.EnvironmentVariables["SPRING_SYSTEM_PROMPT"] == expectedPrompt),
            Arg.Any<CancellationToken>());
    }
}
