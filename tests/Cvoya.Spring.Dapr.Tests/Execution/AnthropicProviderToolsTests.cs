// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Net;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Execution;

using FluentAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Xunit;

/// <summary>
/// Unit tests for <see cref="AnthropicProvider.CompleteWithToolsAsync"/>.
/// </summary>
public class AnthropicProviderToolsTests
{
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IOptions<AiProviderOptions> _options;

    public AnthropicProviderToolsTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _options = Options.Create(new AiProviderOptions
        {
            ApiKey = "test-api-key",
            Model = "claude-sonnet-4-20250514",
            MaxTokens = 1024,
            BaseUrl = "https://api.anthropic.com",
        });
    }

    private AnthropicProvider CreateProvider(CapturingHttpMessageHandler handler)
        => new(new HttpClient(handler), _options, _loggerFactory);

    private static ToolDefinition CreateEchoTool() => new(
        "echo",
        "Echoes its input back.",
        JsonSerializer.SerializeToElement(new
        {
            type = "object",
            properties = new { text = new { type = "string" } },
            required = new[] { "text" },
        }));

    private static IReadOnlyList<ConversationTurn> CreateUserTurn(string text) =>
    [
        new ConversationTurn("user", [new ContentBlock.TextBlock(text)])
    ];

    [Fact]
    public async Task CompleteWithToolsAsync_SendsToolsArrayInRequest()
    {
        var responseJson = JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = "hi" } },
            stop_reason = "end_turn",
            usage = new { input_tokens = 5, output_tokens = 2 },
        });
        var handler = new CapturingHttpMessageHandler(responseJson, HttpStatusCode.OK);
        var provider = CreateProvider(handler);
        var tool = CreateEchoTool();

        await provider.CompleteWithToolsAsync(
            CreateUserTurn("say hi"),
            [tool],
            TestContext.Current.CancellationToken);

        handler.LastRequestBody.Should().NotBeNull();
        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        body.TryGetProperty("tools", out var toolsEl).Should().BeTrue();
        toolsEl.ValueKind.Should().Be(JsonValueKind.Array);
        toolsEl.GetArrayLength().Should().Be(1);
        toolsEl[0].GetProperty("name").GetString().Should().Be("echo");
        toolsEl[0].GetProperty("description").GetString().Should().Be("Echoes its input back.");
        toolsEl[0].TryGetProperty("input_schema", out _).Should().BeTrue();

        body.TryGetProperty("messages", out var messages).Should().BeTrue();
        messages.GetArrayLength().Should().Be(1);
        messages[0].GetProperty("role").GetString().Should().Be("user");
        var firstContent = messages[0].GetProperty("content")[0];
        firstContent.GetProperty("type").GetString().Should().Be("text");
        firstContent.GetProperty("text").GetString().Should().Be("say hi");
    }

    [Fact]
    public async Task CompleteWithToolsAsync_PlainTextResponse_PopulatesText()
    {
        var responseJson = JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = "Greetings." } },
            stop_reason = "end_turn",
            usage = new { input_tokens = 1, output_tokens = 1 },
        });
        var handler = new CapturingHttpMessageHandler(responseJson, HttpStatusCode.OK);
        var provider = CreateProvider(handler);

        var result = await provider.CompleteWithToolsAsync(
            CreateUserTurn("hello"),
            [CreateEchoTool()],
            TestContext.Current.CancellationToken);

        result.Text.Should().Be("Greetings.");
        result.ToolCalls.Should().BeEmpty();
        result.StopReason.Should().Be("end_turn");
    }

    [Fact]
    public async Task CompleteWithToolsAsync_ToolUseResponse_PopulatesToolCalls()
    {
        var responseJson = JsonSerializer.Serialize(new
        {
            content = new object[]
            {
                new { type = "text", text = "Let me check." },
                new
                {
                    type = "tool_use",
                    id = "toolu_abc",
                    name = "echo",
                    input = new { text = "ping" },
                },
            },
            stop_reason = "tool_use",
            usage = new { input_tokens = 5, output_tokens = 3 },
        });
        var handler = new CapturingHttpMessageHandler(responseJson, HttpStatusCode.OK);
        var provider = CreateProvider(handler);

        var result = await provider.CompleteWithToolsAsync(
            CreateUserTurn("echo ping"),
            [CreateEchoTool()],
            TestContext.Current.CancellationToken);

        result.StopReason.Should().Be("tool_use");
        result.Text.Should().Be("Let me check.");
        result.ToolCalls.Should().HaveCount(1);
        var call = result.ToolCalls[0];
        call.Id.Should().Be("toolu_abc");
        call.Name.Should().Be("echo");
        call.Input.GetProperty("text").GetString().Should().Be("ping");
    }

    [Fact]
    public async Task CompleteWithToolsAsync_SerialisesToolResultBlocks()
    {
        var responseJson = JsonSerializer.Serialize(new
        {
            content = new[] { new { type = "text", text = "done" } },
            stop_reason = "end_turn",
            usage = new { input_tokens = 1, output_tokens = 1 },
        });
        var handler = new CapturingHttpMessageHandler(responseJson, HttpStatusCode.OK);
        var provider = CreateProvider(handler);

        IReadOnlyList<ConversationTurn> turns =
        [
            new ConversationTurn("user", [new ContentBlock.TextBlock("run the tool")]),
            new ConversationTurn("assistant",
            [
                new ContentBlock.ToolUseBlock(
                    "toolu_1",
                    "echo",
                    JsonSerializer.SerializeToElement(new { text = "hi" })),
            ]),
            new ConversationTurn("user",
            [
                new ContentBlock.ToolResultBlock("toolu_1", "hi", false),
            ]),
        ];

        await provider.CompleteWithToolsAsync(turns, [CreateEchoTool()], TestContext.Current.CancellationToken);

        var body = JsonDocument.Parse(handler.LastRequestBody!).RootElement;
        var messages = body.GetProperty("messages");
        messages.GetArrayLength().Should().Be(3);

        var assistantContent = messages[1].GetProperty("content")[0];
        assistantContent.GetProperty("type").GetString().Should().Be("tool_use");
        assistantContent.GetProperty("id").GetString().Should().Be("toolu_1");
        assistantContent.GetProperty("name").GetString().Should().Be("echo");

        var resultContent = messages[2].GetProperty("content")[0];
        resultContent.GetProperty("type").GetString().Should().Be("tool_result");
        resultContent.GetProperty("tool_use_id").GetString().Should().Be("toolu_1");
        resultContent.GetProperty("content").GetString().Should().Be("hi");
        resultContent.GetProperty("is_error").GetBoolean().Should().BeFalse();
    }

    private sealed class CapturingHttpMessageHandler(string responseContent, HttpStatusCode statusCode) : HttpMessageHandler
    {
        public string? LastRequestBody { get; private set; }

        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseContent, Encoding.UTF8, "application/json"),
            };
        }
    }
}