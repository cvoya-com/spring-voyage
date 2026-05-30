// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.GitHubApp;

using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Small loopback HTTP server that drives the browser side of GitHub's
/// App-from-manifest handshake. It plays two roles on the same ephemeral
/// port: first it serves an auto-submitting HTML form that POSTs the manifest
/// to GitHub (GitHub has no GET query-string variant — see
/// <see cref="GitHubAppManifest"/>), then it captures the
/// <c>?code=…&amp;state=…</c> GitHub appends when it redirects the browser
/// back after the operator clicks <b>Create</b>. The ephemeral-port binding
/// pattern is lifted from <c>McpServer</c> (#595 / PR #617): bind to port 0,
/// read the OS-assigned port back, retry a handful of times on
/// Address-In-Use collisions so a heavily loaded dev laptop doesn't
/// spuriously fail the verb.
/// </summary>
/// <remarks>
/// The listener serves the form page on the initial navigation, answers
/// incidental requests (favicon, etc.) with 204, and returns as soon as a
/// request carries a <c>code</c> whose <c>state</c> matches the nonce we
/// issued. A mismatched <c>state</c> is treated as a stray / forged hit and
/// ignored — the unguessable nonce is what protects the one-time code
/// exchange during the wait window.
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
    /// Serves <paramref name="formHtml"/> (the auto-submitting manifest form)
    /// on the initial browser navigation, then waits for GitHub to redirect
    /// back with <c>?code=…</c>. Returns the conversion code once a request
    /// arrives whose <c>state</c> matches <paramref name="expectedState"/>.
    /// Incidental no-code requests (favicon, refreshes) are answered without
    /// ending the wait. Times out per <paramref name="timeout"/>; returns
    /// <c>null</c> on timeout so the caller can render a resumable error
    /// rather than throwing.
    /// </summary>
    /// <param name="listener">A started loopback listener.</param>
    /// <param name="formHtml">
    /// HTML served on the root navigation — see
    /// <see cref="GitHubAppManifest.BuildAutoSubmitFormHtml"/>.
    /// </param>
    /// <param name="expectedState">
    /// The CSRF nonce embedded in the form's POST action. A redirect-back
    /// whose <c>state</c> does not match is ignored. Pass an empty string to
    /// skip state verification (the legacy first-code-wins behaviour).
    /// </param>
    /// <param name="timeout">Total wait window across all requests.</param>
    /// <param name="cancellationToken">Caller cancellation.</param>
    public static async Task<string?> WaitForCallbackCodeAsync(
        HttpListener listener,
        string formHtml,
        string expectedState,
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
            // HttpListener.GetContextAsync isn't cancellable; Abort() is the
            // escape hatch. Register a callback that aborts the listener when
            // our combined token fires — that surfaces as a HttpListenerException
            // out of GetContextAsync below, which we treat as a timeout.
            using var cancelReg = timeoutCts.Token.Register(() =>
            {
                try { listener.Abort(); } catch { /* best-effort */ }
            });

            while (true)
            {
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

                var request = context.Request;
                var code = request.QueryString["code"];

                if (!string.IsNullOrWhiteSpace(code))
                {
                    // GitHub echoes the nonce we put on the POST action URL.
                    // A mismatch means this isn't our redirect — ignore it
                    // and keep waiting rather than exchanging a foreign code.
                    var state = request.QueryString["state"];
                    if (!string.IsNullOrEmpty(expectedState)
                        && !string.Equals(state, expectedState, StringComparison.Ordinal))
                    {
                        await RespondHtmlAsync(
                            context,
                            HttpStatusCode.BadRequest,
                            SuccessHtml(
                                "Unexpected callback",
                                "This request did not originate from <code>spring github-app register</code>. " +
                                "You can close this tab."),
                            cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    await RespondHtmlAsync(
                        context,
                        HttpStatusCode.OK,
                        SuccessHtml(
                            "Spring Voyage — GitHub App registered",
                            "You can close this tab. The CLI is finishing the handshake with GitHub."),
                        cancellationToken).ConfigureAwait(false);
                    return code;
                }

                // No code yet. The initial navigation lands on "/" — serve the
                // auto-submitting manifest form there. Everything else
                // (favicon, stray probes) gets a 204 so the wait continues.
                if (string.Equals(request.Url?.AbsolutePath, "/", StringComparison.Ordinal))
                {
                    await RespondHtmlAsync(context, HttpStatusCode.OK, formHtml, cancellationToken)
                        .ConfigureAwait(false);
                }
                else
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NoContent;
                    context.Response.OutputStream.Close();
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task RespondHtmlAsync(
        HttpListenerContext context,
        HttpStatusCode statusCode,
        string html,
        CancellationToken cancellationToken)
    {
        var response = context.Response;
        response.StatusCode = (int)statusCode;
        response.ContentType = "text/html; charset=utf-8";
        var buffer = System.Text.Encoding.UTF8.GetBytes(html);
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        response.OutputStream.Close();
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
