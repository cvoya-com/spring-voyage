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
/// In-process HTTP listener that stands in for the Spring Voyage host
/// API during tests. Routes requests by (method, path-prefix) and
/// records each request so assertions can verify the CLI hit the
/// expected endpoints in the expected order.
/// </summary>
internal sealed class MockSpringApiServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly Task _loop;
    private readonly List<RouteRule> _routes;
    private readonly List<ReceivedRequest> _received = new();
    private readonly object _sync = new();

    public string BaseUrl { get; }

    public IReadOnlyList<ReceivedRequest> Received
    {
        get
        {
            lock (_sync)
            {
                return _received.ToArray();
            }
        }
    }

    private MockSpringApiServer(HttpListener listener, string baseUrl, IEnumerable<RouteRule> routes)
    {
        _listener = listener;
        BaseUrl = baseUrl;
        _routes = new List<RouteRule>(routes);
        _loop = Task.Run(LoopAsync);
    }

    public static Task<MockSpringApiServer> StartAsync(IEnumerable<RouteRule> routes)
    {
        var (listener, port) = CallbackListener.BindHttpListenerWithRetry();
        var baseUrl = $"http://127.0.0.1:{port}";
        return Task.FromResult(new MockSpringApiServer(listener, baseUrl, routes));
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
            var method = ctx.Request.HttpMethod;

            lock (_sync)
            {
                _received.Add(new ReceivedRequest(method, path, body));
            }

            var match = MatchRoute(method, path);
            if (match is null)
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.NotFound;
                ctx.Response.Close();
                continue;
            }

            var (status, responseJson) = match.Handler(method, path, body);
            var bytes = System.Text.Encoding.UTF8.GetBytes(responseJson);
            ctx.Response.StatusCode = (int)status;
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            if (bytes.Length > 0)
            {
                await ctx.Response.OutputStream.WriteAsync(bytes);
            }
            ctx.Response.OutputStream.Close();
        }
    }

    private RouteRule? MatchRoute(string method, string path)
    {
        foreach (var rule in _routes)
        {
            if (!string.Equals(rule.Method, method, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (rule.MatchKind == RouteMatch.Exact && string.Equals(rule.Path, path, StringComparison.Ordinal))
            {
                return rule;
            }
            if (rule.MatchKind == RouteMatch.Prefix && path.StartsWith(rule.Path, StringComparison.Ordinal))
            {
                return rule;
            }
        }
        return null;
    }

    public void Dispose()
    {
        try { _listener.Stop(); } catch { }
        try { ((IDisposable)_listener).Dispose(); } catch { }
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { }
    }

    public delegate (HttpStatusCode Status, string ResponseJson) RouteHandler(string method, string path, string requestBody);

    public enum RouteMatch
    {
        Exact,
        Prefix,
    }

    public sealed record RouteRule(string Method, string Path, RouteMatch MatchKind, RouteHandler Handler);

    public sealed record ReceivedRequest(string Method, string Path, string Body);
}
