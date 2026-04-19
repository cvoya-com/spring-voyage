// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.GitHubApp;

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Small loopback HTTP server that waits for GitHub to redirect the
/// browser back with a <c>?code=...</c> query string after the user
/// confirms App creation. The ephemeral-port binding pattern is lifted
/// from <c>McpServer</c> (#595 / PR #617): bind to port 0, read the
/// OS-assigned port back, retry a handful of times on Address-In-Use
/// collisions so a heavily loaded dev laptop doesn't spuriously fail
/// the verb.
/// </summary>
/// <remarks>
/// The listener scope is intentionally tiny — it serves exactly one
/// request, replies with a small success page, and shuts down. It does
/// NOT handle concurrency, re-entrancy, or long-running serving. If a
/// curious actor POSTs to the port before GitHub redirects, the first
/// arrival wins — the verb's correctness hinges on the operator not
/// hand-crafting requests at the callback URL during the 5-minute
/// window.
/// </remarks>
public static class CallbackListener
{
    /// <summary>Default number of port-bind attempts — matches issue spec.</summary>
    public const int DefaultMaxBindAttempts = 3;

    /// <summary>Default listener wait window after the browser hands off.</summary>
    public static readonly TimeSpan DefaultCallbackTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Picks an OS-assigned ephemeral port on 127.0.0.1 by binding a
    /// throwaway <see cref="TcpListener"/> to port 0 and reading back the
    /// assigned slot. The port can race with another binder between
    /// <see cref="TcpListener.Stop"/> and the caller's bind; retry loops
    /// in <see cref="BindHttpListenerWithRetry"/> swallow the TOCTOU.
    /// </summary>
    public static int PickFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    /// <summary>
    /// Binds a <see cref="HttpListener"/> on an ephemeral loopback port,
    /// retrying up to <paramref name="maxAttempts"/> times on address-in-use
    /// collisions. Returns the bound listener and the chosen port.
    /// </summary>
    /// <exception cref="HttpListenerException">
    /// Thrown when every attempt fails; the inner <c>ErrorCode</c> is the
    /// last OS error observed.
    /// </exception>
    public static (HttpListener Listener, int Port) BindHttpListenerWithRetry(
        int maxAttempts = DefaultMaxBindAttempts,
        Func<int>? portPicker = null)
    {
        if (maxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), maxAttempts, "Must be >= 1.");
        }
        portPicker ??= PickFreePort;

        HttpListenerException? lastException = null;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var port = portPicker();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (HttpListenerException ex)
            {
                lastException = ex;
                SafeAbort(listener);
                // Short backoff — collisions are typically resolved in
                // milliseconds once the neighbouring binder takes its
                // port. 50/100/200 ms caps retry time at <300ms worst-case.
                if (attempt + 1 < maxAttempts)
                {
                    Thread.Sleep(50 * (1 << attempt));
                }
            }
        }

        throw new HttpListenerException(
            lastException?.ErrorCode ?? 0,
            $"Failed to bind loopback callback listener after {maxAttempts} attempts. " +
            $"Last error: {lastException?.Message}");
    }

    private static void SafeAbort(HttpListener listener)
    {
        // HttpListener.Close/Dispose on a listener that never started can
        // itself throw HttpListenerException on some platforms. Swallow;
        // the socket was never adopted, so teardown noise is harmless.
        try { listener.Abort(); } catch { /* best-effort */ }
        try { ((IDisposable)listener).Dispose(); } catch { /* best-effort */ }
    }

    /// <summary>
    /// Waits for a single <c>GET /?code=...</c> request and returns the
    /// code. Times out per <paramref name="timeout"/>; returns
    /// <c>null</c> on timeout so the caller can render a resumable error
    /// rather than throwing.
    /// </summary>
    public static async Task<string?> WaitForCallbackCodeAsync(
        HttpListener listener,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(listener);
        if (!listener.IsListening)
        {
            throw new InvalidOperationException("Listener is not running.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            // HttpListener.GetContextAsync isn't cancellable; Abort() is
            // the escape hatch. Register a callback that aborts the
            // listener when our combined token fires.
            using var cancelReg = timeoutCts.Token.Register(() =>
            {
                try { listener.Abort(); } catch { /* best-effort */ }
            });

            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                // Abort() surfaces here as ERROR_OPERATION_ABORTED — treat
                // as timeout.
                return null;
            }
            catch (ObjectDisposedException)
            {
                return null;
            }

            var code = context.Request.QueryString["code"];
            var response = context.Response;
            response.StatusCode = (int)HttpStatusCode.OK;
            response.ContentType = "text/html; charset=utf-8";
            // The page stays on-screen after the redirect; keep it tiny,
            // branded, and dependency-free (no JS, no CSS frameworks).
            var body = string.IsNullOrWhiteSpace(code)
                ? SuccessHtml("Missing code", "GitHub did not include a <code>code</code> query parameter. " +
                    "Close this tab and re-run <code>spring github-app register</code>.")
                : SuccessHtml("Spring Voyage — GitHub App registered",
                    "You can close this tab. The CLI is finishing the handshake with GitHub.");
            var buffer = System.Text.Encoding.UTF8.GetBytes(body);
            response.ContentLength64 = buffer.Length;
            await response.OutputStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
            response.OutputStream.Close();
            return code;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static string SuccessHtml(string title, string message)
    {
        // Kept as a plain string concatenation rather than an interpolated
        // raw string — interpolation + `{ }` inside CSS requires juggling
        // `$$"""..."""` that's worse to read than the concat below.
        var encodedTitle = WebUtility.HtmlEncode(title);
        return "<!doctype html>\n"
            + "<html lang=\"en\">\n"
            + "<head>\n"
            + "  <meta charset=\"utf-8\">\n"
            + "  <title>" + encodedTitle + "</title>\n"
            + "  <style>\n"
            + "    body { font-family: system-ui, -apple-system, sans-serif; max-width: 40rem; margin: 4rem auto; padding: 0 1rem; color: #1f2328; }\n"
            + "    h1 { font-size: 1.25rem; }\n"
            + "    code { background: #f3f4f6; padding: 0.1rem 0.3rem; border-radius: 3px; }\n"
            + "  </style>\n"
            + "</head>\n"
            + "<body>\n"
            + "  <h1>" + encodedTitle + "</h1>\n"
            + "  <p>" + message + "</p>\n"
            + "</body>\n"
            + "</html>\n";
    }
}