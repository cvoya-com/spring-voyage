// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Net;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Dapr.Execution;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for <see cref="ModelCatalog"/>. Covers dynamic-fetch happy path,
/// static fallback on every failure mode, per-provider caching, and the Ollama
/// branch. See issue #597.
/// </summary>
public class ModelCatalogTests
{
    // After #615 ModelCatalog reads credentials through the tier-2 resolver
    // rather than AiProviderOptions.ApiKey. The ApiKey property remains on
    // AiProviderOptions for backward compatibility with in-process
    // AnthropicProvider callers that have not yet migrated, but the
    // catalog ignores it.
    private static readonly IOptions<AiProviderOptions> AnthropicOptionsWithKey = Options.Create(
        new AiProviderOptions
        {
            BaseUrl = "https://api.anthropic.example",
        });

    private static readonly IOptions<AiProviderOptions> AnthropicOptionsWithoutKey = Options.Create(
        new AiProviderOptions());

    private static Func<CancellationToken, Task<LlmCredentialResolution>> Credential(string? value, string name = "anthropic-api-key") =>
        _ => Task.FromResult(new LlmCredentialResolution(
            value,
            value is null ? LlmCredentialSource.NotFound : LlmCredentialSource.Tenant,
            name));

    private static readonly IOptions<OllamaOptions> OllamaOpts = Options.Create(new OllamaOptions
    {
        BaseUrl = "http://ollama.example",
        HealthCheckTimeoutSeconds = 2,
    });

    [Fact]
    public async Task GetAvailableModelsAsync_Claude_WithApiKey_ReturnsDynamicList()
    {
        var handler = new RouterHandler();
        handler.Add("api.anthropic.example", HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            data = new[]
            {
                new { id = "claude-opus-5-20260101" },
                new { id = "claude-sonnet-5-20260101" },
            },
        }));

        var catalog = CreateCatalog(handler, AnthropicOptionsWithKey, anthropicCredential: "test-api-key");

        var models = await catalog.GetAvailableModelsAsync("claude", TestContext.Current.CancellationToken);

        models.ShouldBe(new[] { "claude-opus-5-20260101", "claude-sonnet-5-20260101" });
        handler.LastRequest!.Headers.GetValues("x-api-key").ShouldContain("test-api-key");
        handler.LastRequest!.Headers.GetValues("anthropic-version").ShouldContain("2023-06-01");
        handler.LastRequest!.RequestUri!.ToString().ShouldBe("https://api.anthropic.example/v1/models");
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Claude_WithoutApiKey_FallsBackToStatic()
    {
        var handler = new RouterHandler();
        var catalog = CreateCatalog(handler, AnthropicOptionsWithoutKey);

        var models = await catalog.GetAvailableModelsAsync("claude", TestContext.Current.CancellationToken);

        models.ShouldBe(ModelCatalog.StaticFallback["claude"]);
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Claude_On401_FallsBackToStatic()
    {
        var handler = new RouterHandler();
        handler.Add("api.anthropic.example", HttpStatusCode.Unauthorized, "{}");

        var catalog = CreateCatalog(handler, AnthropicOptionsWithKey, anthropicCredential: "test-api-key");

        var models = await catalog.GetAvailableModelsAsync("claude", TestContext.Current.CancellationToken);

        models.ShouldBe(ModelCatalog.StaticFallback["claude"]);
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Claude_OnNetworkError_FallsBackToStatic()
    {
        var handler = new ThrowingHandler(new HttpRequestException("boom"));
        var catalog = CreateCatalog(handler, AnthropicOptionsWithKey, anthropicCredential: "test-api-key");

        var models = await catalog.GetAvailableModelsAsync("claude", TestContext.Current.CancellationToken);

        models.ShouldBe(ModelCatalog.StaticFallback["claude"]);
    }

    [Fact]
    public async Task GetAvailableModelsAsync_UnknownProvider_ReturnsEmpty()
    {
        var handler = new RouterHandler();
        var catalog = CreateCatalog(handler, AnthropicOptionsWithoutKey);

        var models = await catalog.GetAvailableModelsAsync(
            "no-such-provider", TestContext.Current.CancellationToken);

        models.ShouldBeEmpty();
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Google_AlwaysStatic()
    {
        var handler = new RouterHandler();
        var catalog = CreateCatalog(handler, AnthropicOptionsWithoutKey);

        var models = await catalog.GetAvailableModelsAsync("google", TestContext.Current.CancellationToken);

        models.ShouldBe(ModelCatalog.StaticFallback["google"]);
        handler.CallCount.ShouldBe(0);
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Ollama_ReturnsTagNames()
    {
        var handler = new RouterHandler();
        handler.Add("ollama.example", HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            models = new[]
            {
                new { name = "llama3.2:3b" },
                new { name = "qwen2.5:14b" },
            },
        }));

        var catalog = CreateCatalog(handler, AnthropicOptionsWithoutKey);

        var models = await catalog.GetAvailableModelsAsync("ollama", TestContext.Current.CancellationToken);

        models.ShouldBe(new[] { "llama3.2:3b", "qwen2.5:14b" });
    }

    [Fact]
    public async Task GetAvailableModelsAsync_CachesByProvider()
    {
        var handler = new RouterHandler();
        handler.Add("api.anthropic.example", HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            data = new[] { new { id = "claude-x" } },
        }));

        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var catalog = CreateCatalog(handler, AnthropicOptionsWithKey, time, anthropicCredential: "test-api-key");

        await catalog.GetAvailableModelsAsync("claude", TestContext.Current.CancellationToken);
        await catalog.GetAvailableModelsAsync("claude", TestContext.Current.CancellationToken);
        await catalog.GetAvailableModelsAsync("claude", TestContext.Current.CancellationToken);

        handler.CallCount.ShouldBe(1);

        // Advance past the 1-hour TTL and the next call re-fetches.
        time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        await catalog.GetAvailableModelsAsync("claude", TestContext.Current.CancellationToken);
        handler.CallCount.ShouldBe(2);
    }

    [Fact]
    public async Task GetAvailableModelsAsync_CachesEachProviderIndependently()
    {
        var handler = new RouterHandler();
        handler.Add("api.anthropic.example", HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            data = new[] { new { id = "claude-x" } },
        }));

        var catalog = CreateCatalog(handler, AnthropicOptionsWithKey, anthropicCredential: "test-api-key");

        var claude = await catalog.GetAvailableModelsAsync("claude", TestContext.Current.CancellationToken);
        var google = await catalog.GetAvailableModelsAsync("google", TestContext.Current.CancellationToken);

        claude.ShouldBe(new[] { "claude-x" });
        google.ShouldBe(ModelCatalog.StaticFallback["google"]);
    }

    [Fact]
    public async Task GetAvailableModelsAsync_Claude_EmptyResponseData_FallsBackToStatic()
    {
        var handler = new RouterHandler();
        handler.Add("api.anthropic.example", HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            data = Array.Empty<object>(),
        }));

        var catalog = CreateCatalog(handler, AnthropicOptionsWithKey, anthropicCredential: "test-api-key");

        var models = await catalog.GetAvailableModelsAsync("claude", TestContext.Current.CancellationToken);

        models.ShouldBe(ModelCatalog.StaticFallback["claude"]);
    }

    private static ModelCatalog CreateCatalog(
        HttpMessageHandler handler,
        IOptions<AiProviderOptions> anthropic,
        TimeProvider? timeProvider = null,
        string? anthropicCredential = null,
        string? openAiCredential = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return new ModelCatalog(
            factory,
            anthropic,
            OllamaOpts,
            Credential(anthropicCredential, "anthropic-api-key"),
            Credential(openAiCredential, "openai-api-key"),
            timeProvider ?? TimeProvider.System,
            NullLogger<ModelCatalog>.Instance);
    }

    private sealed class RouterHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (HttpStatusCode, string)> _responses = new();

        public HttpRequestMessage? LastRequest { get; private set; }
        public int CallCount { get; private set; }

        public void Add(string host, HttpStatusCode status, string body) =>
            _responses[host] = (status, body);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;

            var host = request.RequestUri?.Host ?? string.Empty;
            if (!_responses.TryGetValue(host, out var r))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                {
                    Content = new StringContent($"no stub for {host}"),
                });
            }

            return Task.FromResult(new HttpResponseMessage(r.Item1)
            {
                Content = new StringContent(r.Item2, Encoding.UTF8, "application/json"),
            });
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

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}