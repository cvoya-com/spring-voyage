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
/// Unit tests for <see cref="HostedExecutionDispatcher"/>.
/// </summary>
public class HostedExecutionDispatcherTests
{
    private readonly IAiProvider _aiProvider = Substitute.For<IAiProvider>();
    private readonly IPromptAssembler _promptAssembler = Substitute.For<IPromptAssembler>();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly HostedExecutionDispatcher _dispatcher;

    public HostedExecutionDispatcherTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _dispatcher = new HostedExecutionDispatcher(_aiProvider, _promptAssembler, _loggerFactory);
    }

    private static Message CreateMessage(string? conversationId = null)
    {
        return new Message(
            Guid.NewGuid(),
            new Address("agent", "sender"),
            new Address("agent", "receiver"),
            MessageType.Domain,
            conversationId ?? Guid.NewGuid().ToString(),
            JsonSerializer.SerializeToElement(new { text = "hello" }),
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Verifies that hosted mode calls the prompt assembler and then the AI provider.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_HostedMode_CallsAssemblerAndProvider()
    {
        var message = CreateMessage();
        _promptAssembler.AssembleAsync(message, Arg.Any<CancellationToken>())
            .Returns("assembled prompt");
        _aiProvider.CompleteAsync("assembled prompt", Arg.Any<CancellationToken>())
            .Returns("ai response");

        await _dispatcher.DispatchAsync(message, ExecutionMode.Hosted, TestContext.Current.CancellationToken);

        await _promptAssembler.Received(1).AssembleAsync(message, Arg.Any<CancellationToken>());
        await _aiProvider.Received(1).CompleteAsync("assembled prompt", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that delegated mode throws a <see cref="SpringException"/>.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_DelegatedMode_ThrowsSpringException()
    {
        var message = CreateMessage();

        var act = () => _dispatcher.DispatchAsync(message, ExecutionMode.Delegated, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<SpringException>()
            .WithMessage("*Hosted*Delegated*");
    }

    /// <summary>
    /// Verifies that the AI response is correctly wrapped in a <see cref="Message"/>.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_ReturnsResponseMessage()
    {
        var message = CreateMessage("conv-123");
        _promptAssembler.AssembleAsync(message, Arg.Any<CancellationToken>())
            .Returns("prompt");
        _aiProvider.CompleteAsync("prompt", Arg.Any<CancellationToken>())
            .Returns("response text");

        var result = await _dispatcher.DispatchAsync(message, ExecutionMode.Hosted, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.From.Should().Be(message.To);
        result.To.Should().Be(message.From);
        result.Type.Should().Be(MessageType.Domain);
        result.ConversationId.Should().Be("conv-123");

        var payload = result.Payload.Deserialize<JsonElement>();
        payload.GetProperty("text").GetString().Should().Be("response text");
    }

    /// <summary>
    /// Verifies that cancellation is properly propagated.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_CancellationRequested_PropagatesCancellation()
    {
        var message = CreateMessage();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        _promptAssembler.AssembleAsync(message, Arg.Any<CancellationToken>())
            .Returns<string>(callInfo =>
            {
                callInfo.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return "prompt";
            });

        var act = () => _dispatcher.DispatchAsync(message, ExecutionMode.Hosted, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
