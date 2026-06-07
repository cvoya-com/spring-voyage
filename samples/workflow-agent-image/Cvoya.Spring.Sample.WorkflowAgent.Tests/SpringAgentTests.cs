// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Sample.WorkflowAgent.Tests;

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.AgentSdk;

using Shouldly;

using Xunit;

/// <summary>
/// ADR-0054: <see cref="SpringAgent.FromEnvironment(string?)"/> builds the
/// <see cref="IMessagingClient"/> from the platform MCP environment contract
/// (<c>SPRING_MCP_URL</c> / <c>SPRING_MCP_TOKEN</c>). The per-turn callback
/// JWT and the <c>message.metadata.callbackToken</c> override are retired —
/// the MCP session token is minted per turn and revoked on turn-end.
/// </summary>
public class SpringAgentTests
{
    private static readonly SemaphoreSlim EnvironmentLock = new(1, 1);

    [Fact]
    public async Task FromEnvironment_SendAsync_UsesTheMcpSessionTokenAndJsonRpcTransport()
    {
        await EnvironmentLock.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            await using var server = RecordingMcpServer.Start(TestContext.Current.CancellationToken);
            using var _ = new EnvironmentScope(
                ("SPRING_MCP_URL", server.BaseUrl),
                ("SPRING_MCP_TOKEN", "mcp-session-token"));

            // The inbound-message-body parameter is retained for source
            // compatibility but ignored — there is no per-message token to
            // prefer over the session token.
            var client = SpringAgent.FromEnvironment(
                """{ "message": { "parts": [ { "kind": "text", "text": "turn" } ] } }""");

            var response = await client.SendAsync(
                "11111111-1111-1111-1111-111111111111",
                "22222222-2222-2222-2222-222222222222",
                "deliver this",
                TestContext.Current.CancellationToken);

            server.AuthorizationHeader.ShouldBe("Bearer mcp-session-token");
            server.RequestedMethod.ShouldBe("tools/call");
            server.RequestedToolName.ShouldBe("sv.messaging.send");
            response.Delivered.ShouldBeTrue();
        }
        finally
        {
            EnvironmentLock.Release();
        }
    }

    [Fact]
    public async Task FromEnvironment_MissingMcpUrl_Throws()
    {
        await EnvironmentLock.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            using var _ = new EnvironmentScope(
                ("SPRING_MCP_URL", null),
                ("SPRING_MCP_TOKEN", "mcp-session-token"));

            var ex = Should.Throw<MissingCallbackEnvironmentException>(
                () => SpringAgent.FromEnvironment("plain text payload"));
            ex.Message.ShouldContain("SPRING_MCP_URL");
        }
        finally
        {
            EnvironmentLock.Release();
        }
    }

    [Fact]
    public async Task FromEnvironment_MissingMcpToken_Throws()
    {
        await EnvironmentLock.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            using var _ = new EnvironmentScope(
                ("SPRING_MCP_URL", "http://127.0.0.1:9/"),
                ("SPRING_MCP_TOKEN", null));

            var ex = Should.Throw<MissingCallbackEnvironmentException>(
                () => SpringAgent.FromEnvironment((string?)null));
            ex.Message.ShouldContain("SPRING_MCP_TOKEN");
        }
        finally
        {
            EnvironmentLock.Release();
        }
    }

    /// <summary>
    /// A minimal recording server that speaks the MCP JSON-RPC
    /// <c>tools/call</c> shape: it captures the bearer token, method, and
    /// tool name, then returns a delivery-acknowledgement result envelope.
    /// </summary>
    private sealed class RecordingMcpServer : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private Task _requestTask;

        private RecordingMcpServer(TcpListener listener, Task requestTask, string baseUrl)
        {
            _listener = listener;
            _requestTask = requestTask;
            BaseUrl = baseUrl;
        }

        public string BaseUrl { get; }

        public string? AuthorizationHeader { get; private set; }

        public string? RequestedMethod { get; private set; }

        public string? RequestedToolName { get; private set; }

        public static RecordingMcpServer Start(CancellationToken cancellationToken)
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var server = new RecordingMcpServer(
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
            var requestText = await ReadRequestAsync(stream, cancellationToken);

            AuthorizationHeader = requestText
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(line => line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase))
                ?.Split(':', 2)[1]
                .Trim();

            var bodyStart = requestText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
            if (bodyStart >= 0)
            {
                var body = requestText[(bodyStart + 4)..];
                try
                {
                    using var document = JsonDocument.Parse(body);
                    var root = document.RootElement;
                    RequestedMethod = root.TryGetProperty("method", out var method)
                        ? method.GetString()
                        : null;
                    if (root.TryGetProperty("params", out var parameters) &&
                        parameters.TryGetProperty("name", out var name))
                    {
                        RequestedToolName = name.GetString();
                    }
                }
                catch (JsonException)
                {
                    // Leave the captured fields null — the assertion fails meaningfully.
                }
            }

            var ackPayload = JsonSerializer.Serialize(new
            {
                delivered = true,
                messageId = "33333333-3333-3333-3333-333333333333",
                target = "unit:22222222222222222222222222222222",
                threadId = "11111111-1111-1111-1111-111111111111",
            });
            var rpcResponse = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                result = new
                {
                    content = new[] { new { type = "text", text = ackPayload } },
                    isError = false,
                },
            });

            var responseBytes = Encoding.UTF8.GetBytes(rpcResponse);
            var header = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\nConnection: close\r\nContent-Type: application/json\r\n" +
                $"Content-Length: {responseBytes.Length}\r\n\r\n");
            await stream.WriteAsync(header, cancellationToken);
            await stream.WriteAsync(responseBytes, cancellationToken);
        }

        private static async Task<string> ReadRequestAsync(
            NetworkStream stream,
            CancellationToken cancellationToken)
        {
            var buffer = new byte[2048];
            using var received = new MemoryStream();
            while (true)
            {
                var read = await stream.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                received.Write(buffer, 0, read);
                var text = Encoding.UTF8.GetString(received.ToArray());
                var headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd < 0)
                {
                    continue;
                }

                // Read until the declared Content-Length has arrived.
                var contentLength = ParseContentLength(text);
                var bodyBytes = received.Length - (headerEnd + 4);
                if (bodyBytes >= contentLength)
                {
                    return text;
                }
            }

            return Encoding.UTF8.GetString(received.ToArray());
        }

        private static int ParseContentLength(string requestText)
        {
            var line = requestText
                .Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            return line is not null && int.TryParse(line.Split(':', 2)[1].Trim(), out var length)
                ? length
                : 0;
        }
    }

    private sealed class EnvironmentScope : IDisposable
    {
        private readonly (string Name, string? Value)[] _previousValues;

        public EnvironmentScope(params (string Name, string? Value)[] values)
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
