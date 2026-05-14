// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher.Tests;

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

using Shouldly;

using Xunit;

/// <summary>
/// Endpoint tests for <c>POST /v1/llm/forward</c> and
/// <c>POST /v1/llm/forward/stream</c> — the dispatcher-proxied LLM
/// surface that closes the hosted-agent half of ADR 0028 Decision E
/// (#1168). The behaviours pinned here are the wire contract
/// <see cref="DispatcherProxiedLlmDispatcher"/> reconstructs from on
/// the worker side; if any of them changes the worker's LLM-dispatch
/// path silently breaks.
/// </summary>
public class LlmEndpointsTests : IClassFixture<LlmDispatcherWebApplicationFactory>
{
    private readonly LlmDispatcherWebApplicationFactory _factory;

    public LlmEndpointsTests(LlmDispatcherWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateAuthorizedClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", DispatcherWebApplicationFactory.ValidToken);
        return client;
    }

    [Fact]
    public async Task PostForward_WithoutToken_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync("/v1/llm/forward", new
        {
            url = "http://upstream/",
            bodyBase64 = string.Empty,
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PostForward_MissingUrl_Returns400()
    {
        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/llm/forward", new
        {
            url = "",
            bodyBase64 = string.Empty,
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostForward_RelativeUrl_Returns400()
    {
        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/llm/forward", new
        {
            url = "/v1/chat/completions",
            bodyBase64 = string.Empty,
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostForward_NonHttpScheme_Returns400()
    {
        // Reject non-http(s) schemes — file://, ftp://, etc. would otherwise pass
        // Uri.TryCreate(..., UriKind.Absolute, ...) and crash the upstream HttpClient.
        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/llm/forward", new
        {
            url = "file:///etc/passwd",
            bodyBase64 = string.Empty,
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostForward_InvalidBodyBase64_Returns400()
    {
        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/llm/forward", new
        {
            url = "http://upstream/",
            bodyBase64 = "not-base64!@#$",
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostForward_ForwardsBodyAndHeadersToUpstream()
    {
        // The dispatcher must POST the supplied url with the supplied
        // body bytes verbatim, and forward request headers (e.g.
        // x-api-key, anthropic-version) onto the upstream call. This
        // is the contract IAiProvider implementations rely on for
        // managed providers — drop the headers and Anthropic returns
        // 401, drop the body and Ollama returns 400, and either way
        // the failure looks like a transport bug rather than a missing
        // forward.
        var requestBytes = Encoding.UTF8.GetBytes("{\"model\":\"llama3\"}");
        _factory.UpstreamHandler.RespondWith(req =>
        {
            req.Method.ShouldBe(HttpMethod.Post);
            req.RequestUri.ShouldBe(new Uri("http://upstream-test/v1/chat/completions"));

            req.Headers.GetValues("x-api-key").ShouldContain("sk-ant-test");
            req.Headers.GetValues("anthropic-version").ShouldContain("2023-06-01");

            var body = req.Content!.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            body.ShouldBe(requestBytes);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent("{\"ok\":true}"u8.ToArray()),
            };
        });

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/llm/forward", new
        {
            url = "http://upstream-test/v1/chat/completions",
            bodyBase64 = Convert.ToBase64String(requestBytes),
            headers = new Dictionary<string, string>
            {
                ["x-api-key"] = "sk-ant-test",
                ["anthropic-version"] = "2023-06-01",
            },
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("statusCode").GetInt32().ShouldBe(200);
        var bodyDecoded = Convert.FromBase64String(json.GetProperty("bodyBase64").GetString()!);
        Encoding.UTF8.GetString(bodyDecoded).ShouldBe("{\"ok\":true}");
    }

    [Fact]
    public async Task PostForward_PreservesUpstreamNonSuccessStatusVerbatim()
    {
        // The provider layer keys retry / failover decisions on the
        // upstream status code (Anthropic 429 → backoff, 401 → fail
        // fast). Mirroring the upstream status verbatim — rather than
        // collapsing every non-2xx onto 502 — is the contract that
        // makes the provider's own classification work through the
        // proxy.
        _factory.UpstreamHandler.RespondWith(_ =>
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new ByteArrayContent("rate limited"u8.ToArray()),
            });

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/llm/forward", new
        {
            url = "http://upstream-test/v1/messages",
            bodyBase64 = string.Empty,
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("statusCode").GetInt32().ShouldBe((int)HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task PostForward_UpstreamTransportFailure_Returns502()
    {
        // DNS / connection refused / dispatcher-side socket exhaustion
        // all collapse to 502 with an empty body — the same shape the
        // A2A proxy returns. The worker's
        // DispatcherProxiedLlmDispatcher unwraps this to
        // LlmDispatchResponse(502, []) and the calling provider's
        // failover policy decides what to do next.
        _factory.UpstreamHandler.ThrowOnSend(new HttpRequestException("upstream unreachable"));

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/llm/forward", new
        {
            url = "http://upstream-test/v1/messages",
            bodyBase64 = string.Empty,
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        json.GetProperty("statusCode").GetInt32().ShouldBe(502);
        json.GetProperty("bodyBase64").GetString().ShouldBeEmpty();
    }

    [Fact]
    public async Task PostForwardStream_RelaysUpstreamBodyBytesVerbatim()
    {
        // SSE-shaped streaming response — three chunks from the
        // upstream, relayed verbatim. The provider parses SSE event
        // boundaries on its end so this endpoint must not buffer or
        // re-frame the payload.
        var sseBody = "data: {\"delta\":\"hello\"}\n\ndata: {\"delta\":\" world\"}\n\ndata: [DONE]\n\n"u8.ToArray();
        _factory.UpstreamHandler.RespondWith(_ =>
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(sseBody),
            };
            resp.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
            return resp;
        });

        var client = CreateAuthorizedClient();

        using var response = await client.PostAsJsonAsync("/v1/llm/forward/stream", new
        {
            url = "http://upstream-test/v1/messages",
            bodyBase64 = string.Empty,
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/event-stream");
        var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        body.ShouldBe(sseBody);
    }

    [Fact]
    public async Task PostForwardStream_UpstreamNonSuccess_MirrorsStatus()
    {
        // The streaming path returns the upstream non-success status
        // directly — rather than wrapping it in an envelope — so the
        // worker's streaming HttpClient observes a real HTTP failure
        // and the IAiProvider's existing error handling fires.
        _factory.UpstreamHandler.RespondWith(_ =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new ByteArrayContent("nope"u8.ToArray()),
            });

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/llm/forward/stream", new
        {
            url = "http://upstream-test/v1/messages",
            bodyBase64 = string.Empty,
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task PostForwardStream_UpstreamTransportFailure_Returns502()
    {
        _factory.UpstreamHandler.ThrowOnSend(new HttpRequestException("dead"));

        var client = CreateAuthorizedClient();

        var response = await client.PostAsJsonAsync("/v1/llm/forward/stream", new
        {
            url = "http://upstream-test/v1/messages",
            bodyBase64 = string.Empty,
        }, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadGateway);
    }
}

/// <summary>
/// Test factory specialised for <see cref="LlmEndpoints"/>. Replaces
/// the dispatcher's named upstream <see cref="HttpClient"/> with one
/// that routes through <see cref="StubUpstreamHandler"/>, which the
/// per-test <see cref="UpstreamHandler"/> property arranges.
/// </summary>
public sealed class LlmDispatcherWebApplicationFactory : DispatcherWebApplicationFactory
{
    public StubUpstreamHandler UpstreamHandler { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);

        builder.ConfigureServices(services =>
        {
            services.AddTransient<StubUpstreamPrimaryHandlerProvider>(_ =>
                new StubUpstreamPrimaryHandlerProvider(UpstreamHandler));

            services.Configure<HttpClientFactoryOptions>(LlmEndpoints.ForwardingHttpClientName, options =>
            {
                options.HttpMessageHandlerBuilderActions.Add(b =>
                {
                    b.PrimaryHandler = UpstreamHandler;
                });
            });
        });
    }

    /// <summary>
    /// <see cref="HttpMessageHandler"/> that defers every request to a
    /// per-test responder. Lets endpoint tests stub the upstream LLM
    /// without standing up a real HTTP listener.
    /// </summary>
    public sealed class StubUpstreamHandler : HttpMessageHandler
    {
        private Func<HttpRequestMessage, HttpResponseMessage>? _responder;
        private Exception? _throwOnSend;

        public void RespondWith(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
            _throwOnSend = null;
        }

        public void ThrowOnSend(Exception exception)
        {
            _throwOnSend = exception;
            _responder = null;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (_throwOnSend is not null)
            {
                throw _throwOnSend;
            }

            if (_responder is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotImplemented));
            }

            return Task.FromResult(_responder(request));
        }
    }

    private sealed record StubUpstreamPrimaryHandlerProvider(StubUpstreamHandler Handler);
}
