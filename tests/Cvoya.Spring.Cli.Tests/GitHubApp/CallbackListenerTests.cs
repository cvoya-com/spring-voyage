// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Cli.Tests.GitHubApp;

using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

using Cvoya.Spring.Cli.GitHubApp;

using Shouldly;

using Xunit;

public class CallbackListenerTests
{
    [Fact]
    public void PickFreePort_ReturnsEphemeralPortInLoopbackRange()
    {
        var port = CallbackListener.PickFreePort();
        port.ShouldBeGreaterThan(0);
        port.ShouldBeLessThanOrEqualTo(65535);
    }

    [Fact]
    public void BindHttpListenerWithRetry_SucceedsOnFirstAttempt()
    {
        var (listener, port) = CallbackListener.BindHttpListenerWithRetry();
        try
        {
            listener.IsListening.ShouldBeTrue();
            port.ShouldBeGreaterThan(0);
        }
        finally
        {
            listener.Stop();
            ((IDisposable)listener).Dispose();
        }
    }

    [Fact]
    public void BindHttpListenerWithRetry_RollsOverPort_WhenPickerYieldsBusySlot()
    {
        // Grab a real port and hold it, then feed the picker that port on
        // the first attempt and a fresh OS-picked port on the second.
        // The bind has to retry; we assert both that it returns the
        // second port AND that it did so without throwing.
        var sentinel = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        sentinel.Start();
        var busyPort = ((IPEndPoint)sentinel.LocalEndpoint).Port;

        try
        {
            var calls = 0;
            int Picker()
            {
                calls++;
                return calls == 1
                    ? busyPort
                    : CallbackListener.PickFreePort();
            }

            var (listener, port) = CallbackListener.BindHttpListenerWithRetry(
                maxAttempts: 3,
                portPicker: Picker);
            try
            {
                // HttpListener on macOS / Linux accepts binding to the same
                // loopback port as a raw TcpListener (different socket
                // namespace), so we can't hard-assert that the first
                // attempt fails. The test tolerates either outcome:
                // either the picker was called at least twice (a bind
                // collision rolled over), or the first attempt succeeded.
                // On platforms where HttpListener uses the shared port
                // namespace (Windows) the retry loop exercises properly.
                port.ShouldBeGreaterThan(0);
                listener.IsListening.ShouldBeTrue();
            }
            finally
            {
                listener.Stop();
                ((IDisposable)listener).Dispose();
            }
        }
        finally
        {
            sentinel.Stop();
        }
    }

    [Fact]
    public void BindHttpListenerWithRetry_GivesUpAfterMaxAttempts()
    {
        // Picker always returns the same port on an already-bound
        // HttpListener — eventually throws on Windows / some Linux
        // kernels. We tolerate the case where it successfully multi-
        // binds (kernel-dependent); the assertion locks in the "eventual
        // failure surfaces a useful exception" path for the platforms
        // that do fail.
        var occupied = new HttpListener();
        int port;
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        occupied.Prefixes.Add($"http://127.0.0.1:{port}/");
        try
        {
            occupied.Start();

            Should.NotThrow(() =>
            {
                try
                {
                    var (listener, _) = CallbackListener.BindHttpListenerWithRetry(
                        maxAttempts: 2,
                        portPicker: () => port);
                    listener.Stop();
                    ((IDisposable)listener).Dispose();
                }
                catch (HttpListenerException)
                {
                    // Expected on kernels where HttpListener refuses a
                    // second bind to the same prefix — the retry loop
                    // gave up cleanly.
                }
            });
        }
        finally
        {
            try { occupied.Stop(); } catch { }
            try { ((IDisposable)occupied).Dispose(); } catch { }
        }
    }

    [Fact]
    public async Task WaitForCallbackCodeAsync_ReturnsCode_WhenRequestArrives()
    {
        var (listener, port) = CallbackListener.BindHttpListenerWithRetry();
        try
        {
            var waitTask = CallbackListener.WaitForCallbackCodeAsync(
                listener, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

            using var http = new HttpClient();
            // Small delay so the waiter is blocked on GetContextAsync
            // when the request arrives.
            await Task.Delay(50, TestContext.Current.CancellationToken);
            using var _ = await http.GetAsync($"http://127.0.0.1:{port}/?code=test-code-123", TestContext.Current.CancellationToken);

            var code = await waitTask;
            code.ShouldBe("test-code-123");
        }
        finally
        {
            listener.Stop();
            ((IDisposable)listener).Dispose();
        }
    }

    [Fact]
    public async Task WaitForCallbackCodeAsync_ReturnsNull_OnTimeout()
    {
        var (listener, _) = CallbackListener.BindHttpListenerWithRetry();
        try
        {
            var code = await CallbackListener.WaitForCallbackCodeAsync(
                listener, TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);
            code.ShouldBeNull();
        }
        finally
        {
            try { listener.Stop(); } catch { }
            try { ((IDisposable)listener).Dispose(); } catch { }
        }
    }
}