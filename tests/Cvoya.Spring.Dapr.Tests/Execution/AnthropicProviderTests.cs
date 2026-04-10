// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Net;
using System.Text;
using System.Text.Json;
using Cvoya.Spring.Core;
using Cvoya.Spring.Dapr.Execution;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
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
            Model = "claude-sonnet-4-20250514",
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

        result.Should().Be("Hello, world!");
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.GetValues("x-api-key").Should().Contain("test-api-key");
        handler.LastRequest.Headers.GetValues("anthropic-version").Should().Contain("2023-06-01");
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

        await act.Should().ThrowAsync<OperationCanceledException>();
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

        await act.Should().ThrowAsync<SpringException>()
            .WithMessage("*Unauthorized*");
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

        await act.Should().ThrowAsync<SpringException>()
            .WithMessage("*TooManyRequests*3 attempts*");
        handler.CallCount.Should().Be(3);
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
