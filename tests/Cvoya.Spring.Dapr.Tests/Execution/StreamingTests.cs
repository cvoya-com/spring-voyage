// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Net;
using System.Text;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests for streaming functionality in <see cref="AnthropicProvider.StreamCompleteAsync"/>.
/// </summary>
public class StreamingTests
{
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly IOptions<AiProviderOptions> _options;

    public StreamingTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _options = Options.Create(new AiProviderOptions
        {
            ApiKey = "test-api-key",
            Model = "claude-sonnet-4-6",
            MaxTokens = 1024,
            BaseUrl = "https://api.anthropic.com"
        });
    }

    private AnthropicProvider CreateProvider(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        return new AnthropicProvider(httpClient, _options, _loggerFactory);
    }

    private static string BuildSseResponse(params string[] events)
    {
        var sb = new StringBuilder();
        foreach (var evt in events)
        {
            sb.AppendLine($"data: {evt}");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    [Fact]
    public async Task StreamCompleteAsync_TextDelta_YieldsTokenDeltaEvents()
    {
        var sseContent = BuildSseResponse(
            """{"type":"message_start","message":{"usage":{"input_tokens":10}}}""",
            """{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hello"}}""",
            """{"type":"content_block_delta","delta":{"type":"text_delta","text":" world"}}""",
            """{"type":"message_delta","usage":{"output_tokens":5},"delta":{"stop_reason":"end_turn"}}""");

        var handler = new SseHttpMessageHandler(sseContent, HttpStatusCode.OK);
        var provider = CreateProvider(handler);

        var events = new List<StreamEvent>();
        await foreach (var evt in provider.StreamCompleteAsync("test prompt", TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        events.ShouldContain(e => e is StreamEvent.TokenDelta);
        var tokenDeltas = events.OfType<StreamEvent.TokenDelta>().ToList();
        tokenDeltas.Count().ShouldBe(2);
        tokenDeltas[0].Text.ShouldBe("Hello");
        tokenDeltas[1].Text.ShouldBe(" world");

        // Should end with a Completed event
        events.Last().ShouldBeOfType<StreamEvent.Completed>();
        var completed = (StreamEvent.Completed)events.Last();
        completed.InputTokens.ShouldBe(10);
        completed.OutputTokens.ShouldBe(5);
        completed.StopReason.ShouldBe("end_turn");
    }

    [Fact]
    public async Task StreamCompleteAsync_ThinkingDelta_YieldsThinkingDeltaEvents()
    {
        var sseContent = BuildSseResponse(
            """{"type":"message_start","message":{"usage":{"input_tokens":5}}}""",
            """{"type":"content_block_delta","delta":{"type":"thinking_delta","thinking":"Let me think..."}}""",
            """{"type":"content_block_delta","delta":{"type":"text_delta","text":"Answer"}}""",
            """{"type":"message_delta","usage":{"output_tokens":2},"delta":{"stop_reason":"end_turn"}}""");

        var handler = new SseHttpMessageHandler(sseContent, HttpStatusCode.OK);
        var provider = CreateProvider(handler);

        var events = new List<StreamEvent>();
        await foreach (var evt in provider.StreamCompleteAsync("test prompt", TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        events.OfType<StreamEvent.ThinkingDelta>().ShouldHaveSingleItem()
            .Text.ShouldBe("Let me think...");
    }

    [Fact]
    public async Task StreamCompleteAsync_ErrorResponse_ThrowsSpringException()
    {
        var handler = new SseHttpMessageHandler(
            """{"error":{"message":"Bad request"}}""",
            HttpStatusCode.BadRequest);
        var provider = CreateProvider(handler);

        var act = async () =>
        {
            await foreach (var _ in provider.StreamCompleteAsync("test prompt", TestContext.Current.CancellationToken))
            {
                // consume
            }
        };

        var ex = await Should.ThrowAsync<Core.SpringException>(act);
        ex.Message.ShouldContain("BadRequest");
    }

    [Fact]
    public async Task StreamCompleteAsync_CancellationRequested_ThrowsOperationCancelled()
    {
        var sseContent = BuildSseResponse(
            """{"type":"message_start","message":{"usage":{"input_tokens":5}}}""",
            """{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hello"}}""");

        var handler = new SseHttpMessageHandler(sseContent, HttpStatusCode.OK);
        var provider = CreateProvider(handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = async () =>
        {
            await foreach (var _ in provider.StreamCompleteAsync("test prompt", cts.Token))
            {
                // consume
            }
        };

        await Should.ThrowAsync<OperationCanceledException>(act);
    }

    [Fact]
    public async Task StreamCompleteAsync_DoneSignal_StopsStreaming()
    {
        var sseContent = BuildSseResponse(
            """{"type":"message_start","message":{"usage":{"input_tokens":5}}}""",
            """{"type":"content_block_delta","delta":{"type":"text_delta","text":"Hello"}}""",
            "[DONE]");

        var handler = new SseHttpMessageHandler(sseContent, HttpStatusCode.OK);
        var provider = CreateProvider(handler);

        var events = new List<StreamEvent>();
        await foreach (var evt in provider.StreamCompleteAsync("test prompt", TestContext.Current.CancellationToken))
        {
            events.Add(evt);
        }

        // TokenDelta + Completed
        events.OfType<StreamEvent.TokenDelta>().ShouldHaveSingleItem();
        events.Last().ShouldBeOfType<StreamEvent.Completed>();
    }

    /// <summary>
    /// A test HTTP message handler that returns SSE-formatted response content.
    /// </summary>
    private sealed class SseHttpMessageHandler(string responseContent, HttpStatusCode statusCode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseContent, Encoding.UTF8, "text/event-stream")
            };

            return Task.FromResult(response);
        }
    }
}