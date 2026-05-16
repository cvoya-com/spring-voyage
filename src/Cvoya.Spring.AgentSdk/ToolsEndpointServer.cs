// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

using System.Net;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core.Skills;

/// <summary>
/// Minimal HTTP listener that serves the platform-facing
/// <c>GET /a2a/tools</c> endpoint from an <see cref="IToolRegistry"/>.
/// </summary>
/// <remarks>
/// <para>
/// Sub C (#2336) of the Tools wave. The platform-side introspector
/// (<c>IAgentToolsIntrospector</c>) calls <c>GET /a2a/tools</c> on the
/// agent's HTTP listener at deploy time and on image rotation, then
/// caches the array onto the agent's <c>image_tools</c> column.
/// </para>
/// <para>
/// The path prefix matches the existing A2A bridge (the only other
/// platform-facing surface served on the per-agent port) so the agent
/// presents a single coherent listener instead of two parallel ones.
/// </para>
/// <para>
/// The server intentionally uses <see cref="HttpListener"/> rather than
/// pulling in <c>Microsoft.AspNetCore.App</c> — the SDK is a NuGet
/// package every agent image depends on and ASP.NET Core would balloon
/// its closure. The endpoint is read-only, has no authn, and serves a
/// single JSON shape; <see cref="HttpListener"/> is sufficient.
/// </para>
/// </remarks>
public sealed class ToolsEndpointServer : IDisposable
{
    private readonly IToolRegistry _registry;
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    /// <summary>
    /// Path served by this listener. Documented choice: matches the A2A
    /// bridge prefix so introspection lives alongside the existing
    /// platform-facing surface on the same port.
    /// </summary>
    public const string ToolsPath = "/a2a/tools";

    /// <summary>
    /// Constructs a server bound to <paramref name="prefix"/>. Typical
    /// prefix: <c>http://localhost:8999/</c>. The trailing slash is
    /// required by <see cref="HttpListener"/>.
    /// </summary>
    public ToolsEndpointServer(IToolRegistry registry, string prefix)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        _registry = registry;
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
    }

    /// <summary>
    /// Starts the listener and returns. The accept loop runs on a
    /// detached task; the caller's lifetime owns the server via
    /// <see cref="Dispose"/> or <see cref="StopAsync"/>.
    /// </summary>
    public void Start()
    {
        _listener.Start();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>
    /// Stops the listener gracefully. Safe to call more than once.
    /// </summary>
    public async Task StopAsync()
    {
        if (!_cts.IsCancellationRequested)
        {
            _cts.Cancel();
        }
        try
        {
            _listener.Stop();
        }
        catch (ObjectDisposedException)
        {
            // already stopped
        }
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch
            {
                // Accept loop swallows its own exceptions; nothing to
                // surface here.
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
        _cts.Dispose();
        ((IDisposable)_listener).Dispose();
    }

    /// <summary>
    /// Serialises the registry's contents to the wire JSON shape the
    /// platform persists on <c>image_tools</c>. Visible for testing.
    /// </summary>
    public static byte[] SerializeTools(IToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return JsonSerializer.SerializeToUtf8Bytes(
            registry.List(),
            ToolsJsonOptions);
    }

    internal static readonly JsonSerializerOptions ToolsJsonOptions = BuildToolsJsonOptions();

    private static JsonSerializerOptions BuildToolsJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new ToolDefinitionConverter());
        return options;
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                // Listener was stopped — drop out of the loop.
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            try
            {
                HandleRequest(context);
            }
            catch
            {
                // Best-effort; do NOT crash the accept loop on a single
                // bad request.
                try
                {
                    context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                    context.Response.Close();
                }
                catch
                {
                    // ignore — connection may already be torn down
                }
            }
        }
    }

    private void HandleRequest(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        var method = context.Request.HttpMethod;

        if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(path, ToolsPath, StringComparison.Ordinal))
        {
            var body = SerializeTools(_registry);
            context.Response.StatusCode = (int)HttpStatusCode.OK;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.OutputStream.Write(body);
            context.Response.Close();
            return;
        }

        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
        context.Response.Close();
    }
}
