// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Net;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="AnthropicProvider"/>.
/// </summary>
public class AnthropicProviderTests
{
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly ILogger _logger = Substitute.For<ILogger>();
    private readonly IOptions<AiProviderOptions> _options;

    public AnthropicProviderTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(_logger);
        _options = Options.Create(new AiProviderOptions
        {
            ApiKey = "test-api-key",
            Model = "claude-sonnet-4-6",
            MaxTokens = 1024,
            BaseUrl = "https://api.anthropic.com"
        });
    }

    private static string CreateSuccessResponse(string text = "Hello, world!")
    {
        return JsonSerializer.Serialize(new
        {
            content = new[]
            {
                new { type = "text", text }
            },
            usage = new { input_tokens = 10, output_tokens = 25 }
        });
    }

    private AnthropicProvider CreateProvider(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        return new AnthropicProvider(httpClient, _options, _loggerFactory);
    }

    /// <summary>
    /// Verifies that a valid API response is correctly parsed and returned.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_ValidPrompt_ReturnsResponse()
    {
        var handler = new MockHttpMessageHandler(CreateSuccessResponse(), HttpStatusCode.OK);
        var provider = CreateProvider(handler);

        var result = await provider.CompleteAsync("test prompt", TestContext.Current.CancellationToken);

        result.ShouldBe("Hello, world!");
        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Headers.GetValues("x-api-key").ShouldContain("test-api-key");
        handler.LastRequest.Headers.GetValues("anthropic-version").ShouldContain("2023-06-01");
    }

    /// <summary>
    /// Verifies that an Anthropic Platform API key (sk-ant-api…) routes through the
    /// REST path with <c>x-api-key</c> unchanged. Guards the happy path for #981.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_AnthropicApiKey_UsesRestWithXApiKeyHeader()
    {
        var options = Options.Create(new AiProviderOptions
        {
            ApiKey = "sk-ant-api03-valid",
            Model = "claude-sonnet-4-6",
            MaxTokens = 1024,
            BaseUrl = "https://api.anthropic.com"
        });
        var handler = new MockHttpMessageHandler(CreateSuccessResponse(), HttpStatusCode.OK);
        var provider = new AnthropicProvider(new HttpClient(handler), options, _loggerFactory);

        var result = await provider.CompleteAsync("test prompt", TestContext.Current.CancellationToken);

        result.ShouldBe("Hello, world!");
        handler.CallCount.ShouldBe(1);
        handler.LastRequest!.Headers.GetValues("x-api-key").ShouldContain("sk-ant-api03-valid");
    }

    /// <summary>
    /// Verifies that a Claude.ai OAuth token (sk-ant-oat…) surfaces a structured
    /// <see cref="SpringException"/> at dispatch without hitting the REST endpoint,
    /// replacing the silent-502 behaviour reported in #981. OAuth tokens are only
    /// usable through the in-container <c>claude</c> CLI path exposed by
    /// <see cref="Cvoya.Spring.Core.AgentRuntimes.IAgentRuntime"/>, so the REST
    /// provider must fail fast with an operator-actionable message.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_OAuthToken_ThrowsCredentialFormatRejectedWithoutCallingRest()
    {
        var options = Options.Create(new AiProviderOptions
        {
            ApiKey = "sk-ant-oat01-example",
            Model = "claude-sonnet-4-6",
            MaxTokens = 1024,
            BaseUrl = "https://api.anthropic.com"
        });
        var handler = new MockHttpMessageHandler(CreateSuccessResponse(), HttpStatusCode.OK);
        var provider = new AnthropicProvider(new HttpClient(handler), options, _loggerFactory);

        var act = () => provider.CompleteAsync("test prompt", TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<SpringException>(act);
        ex.Message.ShouldContain("CredentialFormatRejected");
        ex.Message.ShouldContain("sk-ant-oat");
        handler.CallCount.ShouldBe(0);
    }

    /// <summary>
    /// Verifies that cancellation is properly propagated to the HTTP request.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_CancellationRequested_ThrowsOperationCancelled()
    {
        var handler = new MockHttpMessageHandler(CreateSuccessResponse(), HttpStatusCode.OK);
        var provider = CreateProvider(handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => provider.CompleteAsync("test prompt", cts.Token);

        await Should.ThrowAsync<OperationCanceledException>(act);
    }

    /// <summary>
    /// Verifies that a non-retryable error response throws a <see cref="SpringException"/>.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_ApiReturnsError_ThrowsSpringException()
    {
        var handler = new MockHttpMessageHandler(
            """{"error":{"message":"Invalid API key"}}""",
            HttpStatusCode.Unauthorized);
        var provider = CreateProvider(handler);

        var act = () => provider.CompleteAsync("test prompt", TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<SpringException>(act);
        ex.Message.ShouldContain("Unauthorized");
    }

    /// <summary>
    /// Verifies that usage statistics are logged after a successful call.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_LogsUsageStats()
    {
        var handler = new MockHttpMessageHandler(CreateSuccessResponse(), HttpStatusCode.OK);
        var provider = CreateProvider(handler);

        await provider.CompleteAsync("test prompt", TestContext.Current.CancellationToken);

        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Is<object>(o => o.ToString()!.Contains("input: 10") && o.ToString()!.Contains("output: 25")),
            Arg.Any<Exception?>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    /// <summary>
    /// Verifies that retryable status codes (429, 5xx) are retried and eventually throw
    /// after max retries are exhausted.
    /// </summary>
    [Fact]
    public async Task CompleteAsync_RetryableStatusCode_RetriesAndThrows()
    {
        var handler = new MockHttpMessageHandler(
            """{"error":{"message":"Rate limited"}}""",
            HttpStatusCode.TooManyRequests);
        var provider = CreateProvider(handler);

        var act = () => provider.CompleteAsync("test prompt", TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<SpringException>(act);
        ex.Message.ShouldContain("TooManyRequests");
        ex.Message.ShouldContain("3 attempts");
        handler.CallCount.ShouldBe(3);
    }

    /// <summary>
    /// A test HTTP message handler that returns a preconfigured response.
    /// </summary>
    private sealed class MockHttpMessageHandler(string responseContent, HttpStatusCode statusCode) : HttpMessageHandler
    {
        /// <summary>
        /// Gets the last HTTP request received by this handler.
        /// </summary>
        public HttpRequestMessage? LastRequest { get; private set; }

        /// <summary>
        /// Gets the total number of calls made to this handler.
        /// </summary>
        public int CallCount { get; private set; }

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;

            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseContent, Encoding.UTF8, "application/json")
            });
        }
    }
}