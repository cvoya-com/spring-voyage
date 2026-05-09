// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Sample.WorkflowAgent.Tests;

using System.Net;
using System.Net.Sockets;
using System.Text;

using Cvoya.Spring.AgentSdk;

using Shouldly;

using Xunit;

public class SpringAgentTests
{
    private static readonly SemaphoreSlim EnvironmentLock = new(1, 1);

    [Fact]
    public async Task FromEnvironment_InboundCallbackToken_PrefersMessageMetadataToken()
    {
        await EnvironmentLock.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var server = RecordingHttpServer.Start(TestContext.Current.CancellationToken);
            using var _ = new EnvironmentScope(
                ("SPRING_CALLBACK_URL", server.BaseUrl),
                ("SPRING_CALLBACK_TOKEN", "stale-launch-token"));

            var client = SpringAgent.FromEnvironment(
                """
                {
                  "callbackToken": "top-level-injected-token",
                  "metadata": {
                    "callbackToken": "metadata-injected-token"
                  },
                  "message": {
                    "callbackToken": "message-injected-token",
                    "metadata": {
                      "callbackToken": "fresh-message-token"
                    },
                    "parts": [
                      { "kind": "text", "text": "turn two" }
                    ]
                  }
                }
                """);

            await client.PostResultAsync("thread-1", "ok", TestContext.Current.CancellationToken);

            server.AuthorizationHeader.ShouldBe("Bearer fresh-message-token");
        }
        finally
        {
            EnvironmentLock.Release();
        }
    }

    [Fact]
    public async Task FromEnvironment_NoInboundCallbackToken_UsesEnvironmentToken()
    {
        await EnvironmentLock.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var server = RecordingHttpServer.Start(TestContext.Current.CancellationToken);
            using var _ = new EnvironmentScope(
                ("SPRING_CALLBACK_URL", server.BaseUrl),
                ("SPRING_CALLBACK_TOKEN", "launch-token"));

            var client = SpringAgent.FromEnvironment("plain text payload");

            await client.PostResultAsync("thread-1", "ok", TestContext.Current.CancellationToken);

            server.AuthorizationHeader.ShouldBe("Bearer launch-token");
        }
        finally
        {
            EnvironmentLock.Release();
        }
    }

    // #2019: explicit first-message coverage — a freshly launched persistent
    // container's first inbound A2A turn carries both a still-valid launch-time
    // SPRING_CALLBACK_TOKEN and a per-message message.metadata.callbackToken.
    // The SDK must prefer the per-message token, and the launch-time env-var
    // must remain set so subsequent turns (or runtimes that ignore the inbound
    // body) continue to bootstrap from it.
    [Fact]
    public async Task FromEnvironment_FirstMessageAfterLaunch_PrefersMessageMetadataTokenAndPreservesEnvironmentBootstrap()
    {
        await EnvironmentLock.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var server = RecordingHttpServer.Start(TestContext.Current.CancellationToken);
            using var _ = new EnvironmentScope(
                ("SPRING_CALLBACK_URL", server.BaseUrl),
                ("SPRING_CALLBACK_TOKEN", "fresh-launch-token"));

            var client = SpringAgent.FromEnvironment(
                """
                {
                  "message": {
                    "metadata": {
                      "callbackToken": "fresh-message-token-turn-1"
                    },
                    "parts": [
                      { "kind": "text", "text": "first turn after launch" }
                    ]
                  }
                }
                """);

            await client.PostResultAsync("thread-1", "ok", TestContext.Current.CancellationToken);

            server.AuthorizationHeader.ShouldBe("Bearer fresh-message-token-turn-1");
            Environment.GetEnvironmentVariable("SPRING_CALLBACK_TOKEN").ShouldBe("fresh-launch-token");
            Environment.GetEnvironmentVariable("SPRING_CALLBACK_URL").ShouldBe(server.BaseUrl);
        }
        finally
        {
            EnvironmentLock.Release();
        }
    }

    private sealed class RecordingHttpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private Task _requestTask;

        private RecordingHttpServer(TcpListener listener, Task requestTask, string baseUrl)
        {
            _listener = listener;
            _requestTask = requestTask;
            BaseUrl = baseUrl;
        }

        public string BaseUrl { get; }

        public string? AuthorizationHeader { get; private set; }

        public static RecordingHttpServer Start(CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = new RecordingHttpServer(
                listener,
                Task.CompletedTask,
                $"http://127.0.0.1:{port}/");

            server._requestTask = server.AcceptOneAsync(cancellationToken);
            return server;
        }

        public async ValueTask DisposeAsync()
        {
            _listener.Stop();
            try
            {
                await _requestTask.WaitAsync(TimeSpan.FromSeconds(1));
            }
            catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or SocketException)
            {
                // Test cleanup after a failed request path.
            }
        }

        private async Task AcceptOneAsync(CancellationToken cancellationToken)
        {
            using var client = await _listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            var headerText = await ReadHeadersAsync(stream, cancellationToken);
            AuthorizationHeader = headerText
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                ?.Split(':', 2)[1]
                .Trim();

            var response = Encoding.ASCII.GetBytes(
                "HTTP/1.1 204 No Content\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
            await stream.WriteAsync(response, cancellationToken);
        }

        private static async Task<string> ReadHeadersAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[1024];
            using var received = new MemoryStream();
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                received.Write(buffer, 0, read);
                var text = Encoding.ASCII.GetString(received.ToArray());
                if (text.Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    return text;
                }
            }

            return Encoding.ASCII.GetString(received.ToArray());
        }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly (string Name, string? Value)[] _previousValues;

        public EnvironmentScope(params (string Name, string Value)[] values)
        {
            _previousValues = values
                .Select(value => (value.Name, Environment.GetEnvironmentVariable(value.Name)))
                .ToArray();

            foreach (var (name, value) in values)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _previousValues)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }
}
