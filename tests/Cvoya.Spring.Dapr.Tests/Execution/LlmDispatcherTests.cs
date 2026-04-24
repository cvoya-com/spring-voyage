// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Execution;

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
/// Unit tests for the worker-side <see cref="ILlmDispatcher"/>
/// implementations introduced for ADR 0028 Decision E (#1168).
/// </summary>
public class LlmDispatcherTests
{
    [Fact]
    public async Task HttpClientLlmDispatcher_SendAsync_ForwardsBodyAndHeaders()
    {
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent("{\"ok\":true}"u8.ToArray()),
        });
        var client = new HttpClient(handler);

        var dispatcher = new HttpClientLlmDispatcher(client, NullLoggerFactory.Instance);

        var requestBytes = "{\"model\":\"llama3\"}"u8.ToArray();
        var response = await dispatcher.SendAsync(new LlmDispatchRequest(
            Url: "http://upstream/v1/chat/completions",
            Body: requestBytes,
            Headers: new Dictionary<string, string>
            {
                ["x-api-key"] = "sk-ant-test",
            }), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(200);
        Encoding.UTF8.GetString(response.Body).ShouldBe("{\"ok\":true}");

        handler.LastRequest.ShouldNotBeNull();
        handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        handler.LastRequest.RequestUri.ShouldBe(new Uri("http://upstream/v1/chat/completions"));
        handler.LastRequest.Headers.GetValues("x-api-key").ShouldContain("sk-ant-test");

        handler.LastBody.ShouldBe(requestBytes);
    }

    [Fact]
    public async Task HttpClientLlmDispatcher_SendAsync_DefaultsContentTypeToJson()
    {
        // The seam intentionally fills in Content-Type when the caller
        // doesn't, matching what the providers' JsonContent.Create did
        // before they routed through the seam. Without this the upstream
        // sees an empty Content-Type and Anthropic rejects with 400.
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler);

        var dispatcher = new HttpClientLlmDispatcher(client, NullLoggerFactory.Instance);

        await dispatcher.SendAsync(new LlmDispatchRequest(
            Url: "http://upstream/",
            Body: "{}"u8.ToArray()), TestContext.Current.CancellationToken);

        handler.LastRequest!.Content!.Headers.ContentType!.MediaType.ShouldBe("application/json");
        handler.LastRequest.Content!.Headers.ContentType!.CharSet.ShouldBe("utf-8");
    }

    [Fact]
    public async Task HttpClientLlmDispatcher_SendStreamingAsync_YieldsBodyChunks()
    {
        var bodyBytes = "data: hello\n\ndata: world\n\n"u8.ToArray();
        var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bodyBytes),
        });
        var client = new HttpClient(handler);
        var dispatcher = new HttpClientLlmDispatcher(client, NullLoggerFactory.Instance);

        var collected = new List<byte>();
        await foreach (var chunk in dispatcher.SendStreamingAsync(
            new LlmDispatchRequest(Url: "http://upstream/", Body: "{}"u8.ToArray()),
            TestContext.Current.CancellationToken))
        {
            collected.AddRange(chunk.ToArray());
        }

        collected.ToArray().ShouldBe(bodyBytes);
    }

    [Fact]
    public async Task DispatcherProxiedLlmDispatcher_SendAsync_PostsEnvelopeAndDecodesResponse()
    {
        var capturedRequest = new TaskCompletionSource<HttpRequestMessage>();
        var handler = new RecordingHandler(req =>
        {
            capturedRequest.TrySetResult(req);
            var envelope = new
            {
                statusCode = 200,
                bodyBase64 = Convert.ToBase64String("{\"ok\":true}"u8.ToArray()),
            };
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(envelope),
            };
        });
        var factory = NewFactory(handler);

        var dispatcher = new DispatcherProxiedLlmDispatcher(
            factory,
            Options.Create(new DispatcherClientOptions { BaseUrl = "http://dispatcher/", BearerToken = "tok-xyz" }),
            NullLoggerFactory.Instance);

        var response = await dispatcher.SendAsync(new LlmDispatchRequest(
            Url: "http://upstream/v1/chat/completions",
            Body: "{\"model\":\"llama3\"}"u8.ToArray(),
            Headers: new Dictionary<string, string>
            {
                ["x-api-key"] = "sk-ant-test",
            }), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(200);
        Encoding.UTF8.GetString(response.Body).ShouldBe("{\"ok\":true}");

        var req = await capturedRequest.Task;
        req.Method.ShouldBe(HttpMethod.Post);
        req.RequestUri.ShouldBe(new Uri("http://dispatcher/v1/llm/forward"));
        req.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        req.Headers.Authorization!.Parameter.ShouldBe("tok-xyz");

        var sent = await req.Content!.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        sent.GetProperty("url").GetString().ShouldBe("http://upstream/v1/chat/completions");
        var bodyDecoded = Convert.FromBase64String(sent.GetProperty("bodyBase64").GetString()!);
        Encoding.UTF8.GetString(bodyDecoded).ShouldBe("{\"model\":\"llama3\"}");
        sent.GetProperty("headers").GetProperty("x-api-key").GetString().ShouldBe("sk-ant-test");
    }

    [Fact]
    public async Task DispatcherProxiedLlmDispatcher_SendAsync_TransportFailureCollapsesTo502()
    {
        // The proxied path must hide every "the dispatcher itself is
        // unreachable" failure behind a 502 with empty body so the
        // calling provider's retry / failover policy treats it
        // uniformly with a real upstream 502. Otherwise the worker
        // surfaces an opaque HttpRequestException to provider code that
        // never expected to deal with worker→dispatcher transport
        // semantics.
        var handler = new RecordingHandler(_ => throw new HttpRequestException("dispatcher down"));
        var factory = NewFactory(handler);

        var dispatcher = new DispatcherProxiedLlmDispatcher(
            factory,
            Options.Create(new DispatcherClientOptions { BaseUrl = "http://dispatcher/" }),
            NullLoggerFactory.Instance);

        var response = await dispatcher.SendAsync(
            new LlmDispatchRequest("http://upstream/", []), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(502);
        response.Body.ShouldBeEmpty();
    }

    [Fact]
    public async Task DispatcherProxiedLlmDispatcher_SendAsync_DispatcherNon2xxCollapsesTo502()
    {
        // The dispatcher endpoint never legitimately returns a non-2xx
        // for the *envelope* — it returns 200 with an envelope that
        // carries the upstream status. So a non-2xx HERE is a deeper
        // failure (auth, the dispatcher rejecting the request shape)
        // that the provider has no business mapping; collapse to 502.
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var factory = NewFactory(handler);

        var dispatcher = new DispatcherProxiedLlmDispatcher(
            factory,
            Options.Create(new DispatcherClientOptions { BaseUrl = "http://dispatcher/" }),
            NullLoggerFactory.Instance);

        var response = await dispatcher.SendAsync(
            new LlmDispatchRequest("http://upstream/", []), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(502);
    }

    [Fact]
    public async Task DispatcherProxiedLlmDispatcher_SendStreamingAsync_RelaysBodyBytes()
    {
        var bodyBytes = "data: a\n\ndata: b\n\n"u8.ToArray();
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bodyBytes),
        });
        var factory = NewFactory(handler);

        var dispatcher = new DispatcherProxiedLlmDispatcher(
            factory,
            Options.Create(new DispatcherClientOptions { BaseUrl = "http://dispatcher/" }),
            NullLoggerFactory.Instance);

        var collected = new List<byte>();
        await foreach (var chunk in dispatcher.SendStreamingAsync(
            new LlmDispatchRequest("http://upstream/", "{}"u8.ToArray()),
            TestContext.Current.CancellationToken))
        {
            collected.AddRange(chunk.ToArray());
        }

        collected.ToArray().ShouldBe(bodyBytes);
    }

    [Fact]
    public async Task DispatcherProxiedLlmDispatcher_SendStreamingAsync_DispatcherNon2xxThrows()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway));
        var factory = NewFactory(handler);

        var dispatcher = new DispatcherProxiedLlmDispatcher(
            factory,
            Options.Create(new DispatcherClientOptions { BaseUrl = "http://dispatcher/" }),
            NullLoggerFactory.Instance);

        await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in dispatcher.SendStreamingAsync(
                new LlmDispatchRequest("http://upstream/", []),
                TestContext.Current.CancellationToken))
            {
            }
        });
    }

    [Fact]
    public async Task DispatcherProxiedLlmDispatcher_MissingBaseUrl_Throws()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var factory = NewFactory(handler);

        var dispatcher = new DispatcherProxiedLlmDispatcher(
            factory,
            Options.Create(new DispatcherClientOptions()),
            NullLoggerFactory.Instance);

        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await dispatcher.SendAsync(
                new LlmDispatchRequest("http://upstream/", []),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LlmHttpMessageHandler_SendAsync_AdaptsToILlmDispatcher()
    {
        // Behavioural pin: the handler must call ILlmDispatcher,
        // forward request body bytes verbatim, expose response bytes
        // as a stream, and surface upstream headers like x-api-key on
        // the dispatch envelope so providers don't lose their auth
        // when the seam is interposed.
        var dispatcher = Substitute.For<ILlmDispatcher>();
        var responseBytes = "{\"ok\":true}"u8.ToArray();
        dispatcher.SendStreamingAsync(Arg.Any<LlmDispatchRequest>(), Arg.Any<CancellationToken>())
            .Returns(YieldChunk(responseBytes));

        var handler = new LlmHttpMessageHandler(dispatcher);
        using var client = new HttpClient(handler, disposeHandler: false);

        using var request = new HttpRequestMessage(HttpMethod.Post, "http://upstream/v1/messages");
        request.Headers.Add("x-api-key", "sk-ant-test");
        request.Content = new ByteArrayContent("{\"model\":\"claude\"}"u8.ToArray());
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        var body = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
        body.ShouldBe(responseBytes);

        dispatcher.Received(1).SendStreamingAsync(
            Arg.Is<LlmDispatchRequest>(r =>
                r.Url == "http://upstream/v1/messages"
                && Encoding.UTF8.GetString(r.Body) == "{\"model\":\"claude\"}"
                && r.Headers!.ContainsKey("x-api-key")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LlmHttpMessageHandler_NonPostMethod_Throws()
    {
        var dispatcher = Substitute.For<ILlmDispatcher>();

        using var handler = new LlmHttpMessageHandler(dispatcher);
        using var client = new HttpClient(handler, disposeHandler: false);

        await Should.ThrowAsync<NotSupportedException>(async () =>
            await client.GetAsync("http://upstream/", TestContext.Current.CancellationToken));
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> YieldChunk(byte[] bytes)
    {
        await Task.Yield();
        yield return bytes;
    }

    private static IHttpClientFactory NewFactory(HttpMessageHandler handler)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return factory;
    }

    /// <summary>
    /// <see cref="HttpMessageHandler"/> that captures the request and
    /// returns a canned response (or invokes a per-request responder).
    /// </summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public HttpRequestMessage? LastRequest { get; private set; }
        public byte[]? LastBody { get; private set; }

        public RecordingHandler(HttpResponseMessage canned)
            : this(_ => canned)
        {
        }

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            }
            return _responder(request);
        }
    }
}