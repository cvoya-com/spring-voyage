// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Core.Tests;

using System.Text.Json;

using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Messaging;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for the default <see cref="IReflectionActionHandler"/> implementations
/// (send-message, start-conversation, request-help) and the
/// <see cref="ReflectionActionHandlerRegistry"/> that dispatches between them.
/// </summary>
public class ReflectionActionHandlerTests
{
    private static readonly Address AgentAddress = new("agent", "ada");

    [Fact]
    public async Task SendMessage_ValidPayload_ProducesDomainMessage()
    {
        var outcome = new ReflectionOutcome(
            ShouldAct: true,
            ActionType: "send-message",
            Reasoning: "test",
            ActionPayload: JsonSerializer.SerializeToElement(new
            {
                targetScheme = "agent",
                targetPath = "bob",
                content = "hello there",
                threadId = "conv-42",
            }));

        var handler = new SendMessageReflectionActionHandler();
        var message = await handler.TranslateAsync(AgentAddress, outcome, TestContext.Current.CancellationToken);

        message.ShouldNotBeNull();
        message!.From.ShouldBe(AgentAddress);
        message.To.ShouldBe(new Address("agent", "bob"));
        message.Type.ShouldBe(MessageType.Domain);
        message.ThreadId.ShouldBe("conv-42");
        message.Payload.GetProperty("Content").GetString().ShouldBe("hello there");
    }

    [Fact]
    public async Task SendMessage_MissingContent_ReturnsNull()
    {
        var outcome = new ReflectionOutcome(
            ShouldAct: true,
            ActionType: "send-message",
            ActionPayload: JsonSerializer.SerializeToElement(new
            {
                targetScheme = "agent",
                targetPath = "bob",
            }));

        var handler = new SendMessageReflectionActionHandler();
        var message = await handler.TranslateAsync(AgentAddress, outcome, TestContext.Current.CancellationToken);

        message.ShouldBeNull();
    }

    [Fact]
    public async Task SendMessage_MissingTarget_ReturnsNull()
    {
        var outcome = new ReflectionOutcome(
            ShouldAct: true,
            ActionType: "send-message",
            ActionPayload: JsonSerializer.SerializeToElement(new
            {
                content = "hello",
            }));

        var handler = new SendMessageReflectionActionHandler();
        var message = await handler.TranslateAsync(AgentAddress, outcome, TestContext.Current.CancellationToken);

        message.ShouldBeNull();
    }

    [Fact]
    public async Task SendMessage_MissingPayload_ReturnsNull()
    {
        var outcome = new ReflectionOutcome(ShouldAct: true, ActionType: "send-message");
        var handler = new SendMessageReflectionActionHandler();

        var message = await handler.TranslateAsync(AgentAddress, outcome, TestContext.Current.CancellationToken);

        message.ShouldBeNull();
    }

    [Fact]
    public async Task StartThread_GeneratesFreshThreadIdWhenAbsent()
    {
        var outcome = new ReflectionOutcome(
            ShouldAct: true,
            ActionType: "start-conversation",
            ActionPayload: JsonSerializer.SerializeToElement(new
            {
                targetScheme = "agent",
                targetPath = "bob",
                topic = "refactor-plan",
            }));

        var handler = new StartConversationReflectionActionHandler();
        var message = await handler.TranslateAsync(AgentAddress, outcome, TestContext.Current.CancellationToken);

        message.ShouldNotBeNull();
        message!.ThreadId.ShouldNotBeNullOrEmpty();
        Guid.TryParse(message.ThreadId, out _).ShouldBeTrue();
        message.Payload.GetProperty("Topic").GetString().ShouldBe("refactor-plan");
        message.Payload.GetProperty("Content").GetString().ShouldBe("refactor-plan");
    }

    [Fact]
    public async Task StartThread_UsesProvidedThreadId()
    {
        var outcome = new ReflectionOutcome(
            ShouldAct: true,
            ActionType: "start-conversation",
            ActionPayload: JsonSerializer.SerializeToElement(new
            {
                targetScheme = "agent",
                targetPath = "bob",
                topic = "refactor-plan",
                content = "First message body.",
                threadId = "conv-stable",
            }));

        var handler = new StartConversationReflectionActionHandler();
        var message = await handler.TranslateAsync(AgentAddress, outcome, TestContext.Current.CancellationToken);

        message.ShouldNotBeNull();
        message!.ThreadId.ShouldBe("conv-stable");
        message.Payload.GetProperty("Content").GetString().ShouldBe("First message body.");
    }

    [Fact]
    public async Task RequestHelp_ShapesPayloadForRequestHelpConsumers()
    {
        var outcome = new ReflectionOutcome(
            ShouldAct: true,
            ActionType: "request-help",
            ActionPayload: JsonSerializer.SerializeToElement(new
            {
                targetScheme = "agent",
                targetPath = "bob",
                reason = "need a review",
            }));

        var handler = new RequestHelpReflectionActionHandler();
        var message = await handler.TranslateAsync(AgentAddress, outcome, TestContext.Current.CancellationToken);

        message.ShouldNotBeNull();
        message!.Payload.GetProperty("RequestHelp").GetBoolean().ShouldBeTrue();
        message.Payload.GetProperty("Reason").GetString().ShouldBe("need a review");
    }

    [Fact]
    public void Registry_Find_ReturnsMatchingHandlerCaseInsensitively()
    {
        var registry = new ReflectionActionHandlerRegistry(new IReflectionActionHandler[]
        {
            new SendMessageReflectionActionHandler(),
            new StartConversationReflectionActionHandler(),
            new RequestHelpReflectionActionHandler(),
        });

        registry.Find("send-message").ShouldNotBeNull();
        registry.Find("SEND-MESSAGE").ShouldNotBeNull();
        registry.Find("start-conversation").ShouldNotBeNull();
        registry.Find("request-help").ShouldNotBeNull();
    }

    [Fact]
    public void Registry_Find_ReturnsNullForUnknownAction()
    {
        var registry = new ReflectionActionHandlerRegistry(new IReflectionActionHandler[]
        {
            new SendMessageReflectionActionHandler(),
        });

        registry.Find("random-unknown").ShouldBeNull();
        registry.Find(null).ShouldBeNull();
        registry.Find(string.Empty).ShouldBeNull();
    }

    [Fact]
    public void Registry_Find_FirstWinsOnDuplicateActionTypes()
    {
        var primary = new NamedHandler("shared");
        var secondary = new NamedHandler("shared");

        var registry = new ReflectionActionHandlerRegistry(new IReflectionActionHandler[]
        {
            primary,
            secondary,
        });

        registry.Find("shared").ShouldBeSameAs(primary);
    }

    private sealed class NamedHandler(string actionType) : IReflectionActionHandler
    {
        public string ActionType { get; } = actionType;

        public Task<Message?> TranslateAsync(
            Address agentAddress, ReflectionOutcome outcome, CancellationToken cancellationToken = default)
            => Task.FromResult<Message?>(null);
    }
}