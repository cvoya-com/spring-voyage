// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Net;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="OllamaProvider"/>.
/// </summary>
public class OllamaProviderTests
{
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly ILogger _logger = Substitute.For<ILogger>();
    private readonly IOptions<OllamaOptions> _options;

    public OllamaProviderTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(_logger);
        _options = Options.Create(new OllamaOptions
        {
            Enabled = true,
            BaseUrl = "http://ollama.test:11434",
            DefaultModel = "llama3.2:3b",
            MaxTokens = 1024
        });
    }

    private static string CreateChatCompletionResponse(string text = "Hello from ollama!")
    {
        return JsonSerializer.Serialize(new
        {
            id = "chatcmpl-1",
            @object = "chat.completion",
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new { role = "assistant", content = text },
                    finish_reason = "stop"
                }
            },
            usage = new { prompt_tokens = 12, completion_tokens = 8, total_tokens = 20 }
        });
    }

    private OllamaProvider CreateProvider(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        return new OllamaProvider(httpClient, _options, _loggerFactory);
    }

    [Fact]
    public async Task CompleteAsync_ValidPrompt_ReturnsAssistantMessage()
    {
        var handler = new CapturingHandler(CreateChatCompletionResponse(), HttpStatusCode.OK);
        var provider = CreateProvider(handler);

        var result = await provider.CompleteAsync("hello", TestContext.Current.CancellationToken);

        result.ShouldBe("Hello from ollama!");
        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.RequestUri!.ToString().ShouldBe("http://ollama.test:11434/v1/chat/completions");
        handler.LastRequest.Headers.Authorization.ShouldBeNull("no API key should be sent to ollama");
    }

    [Fact]
    public async Task CompleteAsync_TrailingSlashInBaseUrl_IsTolerated()
    {
        var options = Options.Create(new OllamaOptions
        {
            Enabled = true,
            BaseUrl = "http://ollama.test:11434/",
            DefaultModel = "llama3.2:3b"
        });
        var handler = new CapturingHandler(CreateChatCompletionResponse(), HttpStatusCode.OK);
        var provider = new OllamaProvider(new HttpClient(handler), options, _loggerFactory);

        await provider.CompleteAsync("hi", TestContext.Current.CancellationToken);

        handler.LastRequest!.RequestUri!.ToString().ShouldBe("http://ollama.test:11434/v1/chat/completions");
    }

    [Fact]
    public async Task CompleteAsync_NonSuccessStatus_ThrowsSpringException()
    {
        var handler = new CapturingHandler(
            """{"error":"model not found"}""",
            HttpStatusCode.NotFound);
        var provider = CreateProvider(handler);

        var act = () => provider.CompleteAsync("hello", TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<SpringException>(act);
        ex.Message.ShouldContain("404");
        ex.Message.ShouldContain("NotFound");
    }

    [Fact]
    public async Task CompleteAsync_CancellationRequested_ThrowsOperationCancelled()
    {
        var handler = new CapturingHandler(CreateChatCompletionResponse(), HttpStatusCode.OK);
        var provider = CreateProvider(handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => provider.CompleteAsync("hello", cts.Token);

        await Should.ThrowAsync<OperationCanceledException>(act);
    }

    [Fact]
    public async Task CompleteAsync_ConnectionFailure_SurfacesAsSpringException()
    {
        var handler = new ThrowingHandler(new HttpRequestException("connection refused"));
        var provider = CreateProvider(handler);

        var act = () => provider.CompleteAsync("hello", TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<SpringException>(act);
        ex.Message.ShouldContain("Ollama request");
        ex.Message.ShouldContain("connection refused");
    }

    [Fact]
    public async Task CompleteAsync_ResponseMissingChoices_ThrowsSpringException()
    {
        var malformed = JsonSerializer.Serialize(new { id = "x" });
        var handler = new CapturingHandler(malformed, HttpStatusCode.OK);
        var provider = CreateProvider(handler);

        var act = () => provider.CompleteAsync("hello", TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<SpringException>(act);
        ex.Message.ShouldContain("did not contain");
    }

    [Fact]
    public async Task CompleteAsync_UsesConfiguredModelInRequestBody()
    {
        var handler = new CapturingHandler(CreateChatCompletionResponse(), HttpStatusCode.OK);
        var provider = CreateProvider(handler);

        await provider.CompleteAsync("hello", TestContext.Current.CancellationToken);

        handler.LastRequestBody.ShouldNotBeNull();
        handler.LastRequestBody!.ShouldContain("\"model\":\"llama3.2:3b\"");
        handler.LastRequestBody.ShouldContain("\"role\":\"user\"");
        handler.LastRequestBody.ShouldContain("\"content\":\"hello\"");
    }

    /// <summary>
    /// Captures every request and returns a canned response. Mirrors the shape of the
    /// helper in <see cref="AnthropicProviderTests"/> so tests read consistently.
    /// </summary>
    private sealed class CapturingHandler(string responseContent, HttpStatusCode statusCode) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseContent, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw exception;
        }
    }
}