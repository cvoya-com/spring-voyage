// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.SlackInstall;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;

using Cvoya.Spring.Cli.GitHubApp;

/// <summary>
/// In-process HTTP listener that stands in for slack.com during tests.
/// Routes by request path and accumulates one record per received
/// request so assertions can inspect them after the flow completes.
/// </summary>
internal sealed class MockSlackServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _loop;
    private readonly ConcurrentDictionary<string, RouteHandler> _routes;
    private readonly List<ReceivedRequest> _received = new();
    private readonly object _receivedSync = new();

    public string BaseUrl { get; }

    public IReadOnlyList<ReceivedRequest> Received
    {
        get
        {
            lock (_receivedSync)
            {
                return _received.ToArray();
            }
        }
    }

    private MockSlackServer(
        HttpListener listener,
        string baseUrl,
        IDictionary<string, RouteHandler> routes)
    {
        _listener = listener;
        BaseUrl = baseUrl;
        _routes = new ConcurrentDictionary<string, RouteHandler>(routes, StringComparer.Ordinal);
        _loop = Task.Run(LoopAsync);
    }

    public static Task<MockSlackServer> StartAsync(IDictionary<string, RouteHandler> routes)
    {
        var (listener, port) = CallbackListener.BindHttpListenerWithRetry();
        var baseUrl = $"http://127.0.0.1:{port}";
        return Task.FromResult(new MockSlackServer(listener, baseUrl, routes));
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream))
            {
                body = await reader.ReadToEndAsync();
            }

            var path = ctx.Request.Url?.AbsolutePath ?? string.Empty;
            var authorization = ctx.Request.Headers["Authorization"];

            lock (_receivedSync)
            {
                _received.Add(new ReceivedRequest(
                    Method: ctx.Request.HttpMethod,
                    Path: path,
                    Authorization: authorization,
                    Body: body));
            }

            if (!_routes.TryGetValue(path, out var handler))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                ctx.Response.Close();
                continue;
            }

            var (status, responseJson) = handler(body);
            var bytes = System.Text.Encoding.UTF8.GetBytes(responseJson);
            ctx.Response.StatusCode = (int)status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.OutputStream.Close();
        }
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { }
        try { ((IDisposable)_listener).Dispose(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    /// <summary>
    /// Per-route handler: takes the raw request body, returns the HTTP
    /// status + response JSON body.
    /// </summary>
    public delegate (HttpStatusCode Status, string ResponseJson) RouteHandler(string requestBody);

    public sealed record ReceivedRequest(
        string Method,
        string Path,
        string? Authorization,
        string Body);
}
