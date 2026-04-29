// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Tools;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Tools;

using global::Dapr.Actors.Runtime;

using Microsoft.Extensions.Logging;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="CheckMessagesTool"/>.
/// </summary>
public class CheckMessagesToolTests
{
    private readonly IActorStateManager _stateManager = Substitute.For<IActorStateManager>();
    private readonly ToolExecutionContextAccessor _contextAccessor = new();
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly CheckMessagesTool _tool;

    public CheckMessagesToolTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _tool = new CheckMessagesTool(_contextAccessor, _loggerFactory);
        _contextAccessor.Current = new ToolExecutionContext(
            new Address("agent", "test-agent"),
            "conv-1",
            _stateManager);
    }

    [Fact]
    public async Task ExecuteAsync_WithMessages_ReturnsAccumulatedMessages()
    {
        var message1 = new Message(
            Guid.NewGuid(),
            new Address("agent", "sender-1"),
            new Address("agent", "test-agent"),
            MessageType.Domain,
            "conv-1",
            JsonSerializer.SerializeToElement(new { Text = "Hello" }),
            DateTimeOffset.UtcNow);

        var message2 = new Message(
            Guid.NewGuid(),
            new Address("agent", "sender-2"),
            new Address("agent", "test-agent"),
            MessageType.Domain,
            "conv-1",
            JsonSerializer.SerializeToElement(new { Text = "World" }),
            DateTimeOffset.UtcNow);

        var channel = new ThreadChannel
        {
            ThreadId = "conv-1",
            Messages = [message1, message2]
        };

        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(true, channel));

        var result = await _tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { }),
            JsonSerializer.SerializeToElement(new { }),
            TestContext.Current.CancellationToken);

        result.ValueKind.ShouldBe(JsonValueKind.Array);
        result.GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public async Task ExecuteAsync_NoMessages_ReturnsEmptyArray()
    {
        _stateManager.TryGetStateAsync<ThreadChannel>(StateKeys.ActiveConversation, Arg.Any<CancellationToken>())
            .Returns(new ConditionalValue<ThreadChannel>(false, default!));

        var result = await _tool.ExecuteAsync(
            JsonSerializer.SerializeToElement(new { }),
            JsonSerializer.SerializeToElement(new { }),
            TestContext.Current.CancellationToken);

        result.ValueKind.ShouldBe(JsonValueKind.Array);
        result.GetArrayLength().ShouldBe(0);
    }
}