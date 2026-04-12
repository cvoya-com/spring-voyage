// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
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
    /// Verifies that hosted mode calls the prompt assembler and then the AI provider
    /// when no tools are advertised (falls through to the single-shot CompleteAsync path).
    /// </summary>
    [Fact]
    public async Task DispatchAsync_HostedMode_CallsAssemblerAndProvider()
    {
        var message = CreateMessage();
        _promptAssembler.AssembleForToolsAsync(message, Arg.Any<CancellationToken>())
            .Returns(new PromptAssemblyResult(
                "assembled prompt",
                Array.Empty<ToolDefinition>(),
                Array.Empty<ConversationTurn>()));
        _aiProvider.CompleteAsync("assembled prompt", Arg.Any<CancellationToken>())
            .Returns("ai response");

        await _dispatcher.DispatchAsync(message, ExecutionMode.Hosted, TestContext.Current.CancellationToken);

        await _promptAssembler.Received(1).AssembleForToolsAsync(message, Arg.Any<CancellationToken>());
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
        _promptAssembler.AssembleForToolsAsync(message, Arg.Any<CancellationToken>())
            .Returns(new PromptAssemblyResult(
                "prompt",
                Array.Empty<ToolDefinition>(),
                Array.Empty<ConversationTurn>()));
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

        _promptAssembler.AssembleForToolsAsync(message, Arg.Any<CancellationToken>())
            .Returns<PromptAssemblyResult>(callInfo =>
            {
                callInfo.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return new PromptAssemblyResult(
                    "prompt",
                    Array.Empty<ToolDefinition>(),
                    Array.Empty<ConversationTurn>());
            });

        var act = () => _dispatcher.DispatchAsync(message, ExecutionMode.Hosted, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    /// Verifies that when the provider returns plain text (no tool_use), the loop exits
    /// immediately and the response text is forwarded unchanged.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_ToolUseLoop_SingleTurnText_ReturnsText()
    {
        var message = CreateMessage();
        var tool = CreateTool("github_read_file");
        _promptAssembler.AssembleForToolsAsync(message, Arg.Any<CancellationToken>())
            .Returns(new PromptAssemblyResult(
                "system",
                [tool],
                [new ConversationTurn("user", [new ContentBlock.TextBlock("hello")])]));
        _aiProvider.CompleteWithToolsAsync(
                Arg.Any<IReadOnlyList<ConversationTurn>>(),
                Arg.Any<IReadOnlyList<ToolDefinition>>(),
                Arg.Any<CancellationToken>())
            .Returns(new AiResponse("all done", Array.Empty<ToolCall>(), "end_turn"));

        var dispatcher = new HostedExecutionDispatcher(
            _aiProvider,
            _promptAssembler,
            Array.Empty<ISkillToolExecutor>(),
            _loggerFactory);

        var result = await dispatcher.DispatchAsync(message, ExecutionMode.Hosted, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Payload.GetProperty("text").GetString().Should().Be("all done");
        await _aiProvider.Received(1).CompleteWithToolsAsync(
            Arg.Any<IReadOnlyList<ConversationTurn>>(),
            Arg.Any<IReadOnlyList<ToolDefinition>>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that a tool_use response is routed to the matching executor and the second
    /// provider call receives the tool_result, producing the final text.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_ToolUseLoop_DispatchesExecutor_AndCompletes()
    {
        var message = CreateMessage();
        var tool = CreateTool("github_read_file");
        _promptAssembler.AssembleForToolsAsync(message, Arg.Any<CancellationToken>())
            .Returns(new PromptAssemblyResult(
                "system",
                [tool],
                [new ConversationTurn("user", [new ContentBlock.TextBlock("read it")])]));

        var toolCall = new ToolCall("toolu_1", "github_read_file", JsonSerializer.SerializeToElement(new { path = "a.txt" }));
        _aiProvider.CompleteWithToolsAsync(
                Arg.Any<IReadOnlyList<ConversationTurn>>(),
                Arg.Any<IReadOnlyList<ToolDefinition>>(),
                Arg.Any<CancellationToken>())
            .Returns(
                _ => new AiResponse(null, [toolCall], "tool_use"),
                _ => new AiResponse("file contents summarised", Array.Empty<ToolCall>(), "end_turn"));

        var executor = new RecordingExecutor("github_", new ToolResult("toolu_1", "file body", false));

        var dispatcher = new HostedExecutionDispatcher(
            _aiProvider,
            _promptAssembler,
            [executor],
            _loggerFactory);

        var result = await dispatcher.DispatchAsync(message, ExecutionMode.Hosted, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.Payload.GetProperty("text").GetString().Should().Be("file contents summarised");
        executor.Invocations.Should().HaveCount(1);
        executor.Invocations[0].Name.Should().Be("github_read_file");

        await _aiProvider.Received(2).CompleteWithToolsAsync(
            Arg.Any<IReadOnlyList<ConversationTurn>>(),
            Arg.Any<IReadOnlyList<ToolDefinition>>(),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Verifies that an unknown tool name produces an error <see cref="ToolResult"/> and
    /// the loop continues until the model emits plain text.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_ToolUseLoop_UnknownTool_ReturnsErrorResultAndContinues()
    {
        var message = CreateMessage();
        var tool = CreateTool("unknown_tool");
        _promptAssembler.AssembleForToolsAsync(message, Arg.Any<CancellationToken>())
            .Returns(new PromptAssemblyResult(
                "system",
                [tool],
                [new ConversationTurn("user", [new ContentBlock.TextBlock("go")])]));

        var capturedTurns = new List<IReadOnlyList<ConversationTurn>>();
        var toolCall = new ToolCall("toolu_x", "unknown_tool", JsonSerializer.SerializeToElement(new { }));
        _aiProvider.CompleteWithToolsAsync(
                Arg.Any<IReadOnlyList<ConversationTurn>>(),
                Arg.Any<IReadOnlyList<ToolDefinition>>(),
                Arg.Any<CancellationToken>())
            .Returns(
                ci => { capturedTurns.Add(((IReadOnlyList<ConversationTurn>)ci[0]).ToArray()); return new AiResponse(null, [toolCall], "tool_use"); },
                ci => { capturedTurns.Add(((IReadOnlyList<ConversationTurn>)ci[0]).ToArray()); return new AiResponse("fallback answer", Array.Empty<ToolCall>(), "end_turn"); });

        var dispatcher = new HostedExecutionDispatcher(
            _aiProvider,
            _promptAssembler,
            Array.Empty<ISkillToolExecutor>(),
            _loggerFactory);

        var result = await dispatcher.DispatchAsync(message, ExecutionMode.Hosted, TestContext.Current.CancellationToken);

        result!.Payload.GetProperty("text").GetString().Should().Be("fallback answer");

        // The second provider call must have received a user turn containing an error tool_result.
        capturedTurns.Should().HaveCount(2);
        var secondCallTurns = capturedTurns[1];
        var resultBlock = secondCallTurns.Last().Content.OfType<ContentBlock.ToolResultBlock>().Single();
        resultBlock.ToolUseId.Should().Be("toolu_x");
        resultBlock.IsError.Should().BeTrue();
        resultBlock.Content.Should().Contain("unknown_tool");
    }

    /// <summary>
    /// Verifies that when the provider always returns tool_use, the loop aborts at the cap
    /// and emits the truncation message.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_ToolUseLoop_IterationCap_Truncates()
    {
        var message = CreateMessage();
        var tool = CreateTool("github_loop");
        _promptAssembler.AssembleForToolsAsync(message, Arg.Any<CancellationToken>())
            .Returns(new PromptAssemblyResult(
                "system",
                [tool],
                [new ConversationTurn("user", [new ContentBlock.TextBlock("go")])]));

        var loopingCall = new ToolCall("toolu_loop", "github_loop", JsonSerializer.SerializeToElement(new { }));
        _aiProvider.CompleteWithToolsAsync(
                Arg.Any<IReadOnlyList<ConversationTurn>>(),
                Arg.Any<IReadOnlyList<ToolDefinition>>(),
                Arg.Any<CancellationToken>())
            .Returns(new AiResponse(null, [loopingCall], "tool_use"));

        var executor = new RecordingExecutor("github_", new ToolResult("toolu_loop", "ok", false));

        var dispatcher = new HostedExecutionDispatcher(
            _aiProvider,
            _promptAssembler,
            [executor],
            _loggerFactory);

        var result = await dispatcher.DispatchAsync(message, ExecutionMode.Hosted, TestContext.Current.CancellationToken);

        result!.Payload.GetProperty("text").GetString().Should().Contain("iteration cap reached");
        await _aiProvider.Received(HostedExecutionDispatcher.MaxToolIterations).CompleteWithToolsAsync(
            Arg.Any<IReadOnlyList<ConversationTurn>>(),
            Arg.Any<IReadOnlyList<ToolDefinition>>(),
            Arg.Any<CancellationToken>());
    }

    private static ToolDefinition CreateTool(string name) =>
        new(name, $"Tool {name}", JsonSerializer.SerializeToElement(new { type = "object" }));

    private sealed class RecordingExecutor(string prefix, ToolResult result) : ISkillToolExecutor
    {
        public List<ToolCall> Invocations { get; } = new();

        public bool CanHandle(string toolName) => toolName.StartsWith(prefix, StringComparison.Ordinal);

        public Task<ToolResult> ExecuteAsync(ToolCall call, CancellationToken cancellationToken)
        {
            Invocations.Add(call);
            return Task.FromResult(result);
        }
    }
}