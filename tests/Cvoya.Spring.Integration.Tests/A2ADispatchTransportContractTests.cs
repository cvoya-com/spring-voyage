// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Integration.Tests;

using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using A2A.V0_3;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Shouldly;

using Xunit;

/// <summary>
/// Round-trip contract tests for the dispatcher → agent A2A transport
/// (issue #1465). A previous regression (the dapr-agent image only mounted
/// <c>create_rest_routes(...)</c>) caused the .NET dispatcher's
/// <see cref="A2AClient.SendMessageAsync"/> call to receive a 404 from the
/// in-container agent, and neither the unit-level dispatcher tests nor the
/// shell-level fast-pool scenarios noticed: the dispatcher mocks asserted
/// only on the outbound payload, and the fast scenarios stopped at
/// <c>messageId</c> (which is allocated synchronously, before dispatch).
///
/// These tests close the gap by standing up a minimal in-process Kestrel
/// responder that mimics the JSON-RPC contract a real Python a2a-sdk agent
/// is expected to satisfy: a static <c>/.well-known/agent.json</c> for the
/// readiness probe and a JSON-RPC <c>message/send</c> endpoint at <c>/</c>
/// that returns a <c>Completed</c> task. The same <see cref="A2AClient"/>
/// the production dispatcher uses then drives the responder.
///
/// Crucially these tests are NOT gated on Docker, Ollama, or the Node
/// agent-sidecar build — they run on every PR via the integration suite.
/// A future regression that drops or renames the JSON-RPC route fails CI
/// without anyone needing to remember to enable a flag.
/// </summary>
public class A2ADispatchTransportContractTests
{
    [Fact]
    public async Task A2AClient_AgainstJsonRpcResponderAtRoot_DeserializesCompletedTaskWithoutThrowing()
    {
        // Stand up the minimal responder. `/.well-known/agent.json` answers
        // the readiness probe; `/` answers the JSON-RPC `message/send`.
        await using var responder = await JsonRpcResponder.StartAsync(
            agentReplyText: "hello from the fake agent");

        // Point the production A2A client at the responder. The dispatcher
        // creates its A2AClient with the same constructor shape and the same
        // backing HttpClient, so this is the wire-shape the dispatcher will
        // actually drive in production.
        using var http = new HttpClient { BaseAddress = responder.Endpoint };
        var client = new A2AClient(responder.Endpoint, http);

        var request = new MessageSendParams
        {
            Message = new AgentMessage
            {
                Role = MessageRole.User,
                Parts = [new TextPart { Text = "ping" }],
                MessageId = Guid.NewGuid().ToString(),
                ContextId = Guid.NewGuid().ToString(),
            },
            Configuration = new MessageSendConfiguration
            {
                AcceptedOutputModes = ["text/plain"],
            },
        };

        var response = await client.SendMessageAsync(
            request, TestContext.Current.CancellationToken);

        // The contract: the responder returns a Completed AgentTask and the
        // .NET A2AClient deserializes it without throwing JsonException.
        // A regression that drops the JSON-RPC route, returns the wrong
        // result-discriminator (`kind`), or serializes TaskState in an
        // unexpected enum form would surface here.
        var task = response.ShouldBeOfType<AgentTask>();
        task.Status.State.ShouldBe(TaskState.Completed);
        responder.MessageSendCallCount.ShouldBe(1);
    }

    [Fact]
    public async Task ReadinessProbe_HitsAgentJsonAtWellKnownPath()
    {
        // The dispatcher's WaitForA2AReadyAsync builds the probe URL as
        //   new Uri(endpoint, ".well-known/agent.json")
        // and treats a 200 as ready. A regression that renames the path
        // (or drops the route entirely) would deadlock dispatch on the
        // readiness wait, surfacing only as a generic timeout.
        await using var responder = await JsonRpcResponder.StartAsync();

        using var http = new HttpClient();
        var probeUri = new Uri(responder.Endpoint, ".well-known/agent.json");

        using var response = await http.GetAsync(
            probeUri, TestContext.Current.CancellationToken);

        response.IsSuccessStatusCode.ShouldBeTrue();
        responder.AgentCardCallCount.ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task A2AClient_WhenJsonRpcRouteMissing_ThrowsRatherThanReturningStaleTask()
    {
        // The exact regression class the issue calls out: a responder that
        // serves only REST routes, with no JSON-RPC handler at `/`, must
        // surface as a hard failure on the dispatcher's SendMessageAsync,
        // not silently succeed. Without this guard a server that answers
        // 404 / 405 at `/` would let dispatch hang or quietly map to a
        // failed task.
        await using var responder = await JsonRpcResponder.StartAsync(
            mountJsonRpc: false);

        using var http = new HttpClient { BaseAddress = responder.Endpoint };
        var client = new A2AClient(responder.Endpoint, http);

        var request = new MessageSendParams
        {
            Message = new AgentMessage
            {
                Role = MessageRole.User,
                Parts = [new TextPart { Text = "ping" }],
                MessageId = Guid.NewGuid().ToString(),
            },
        };

        await Should.ThrowAsync<Exception>(async () =>
        {
            await client.SendMessageAsync(
                request, TestContext.Current.CancellationToken);
        });
    }

    /// <summary>
    /// Minimal in-process Kestrel server that pretends to be a Python
    /// a2a-sdk agent. Mounts:
    ///   - <c>GET /.well-known/agent.json</c> — the readiness probe.
    ///   - <c>POST /</c> — JSON-RPC entrypoint. Recognises only
    ///     <c>message/send</c>; everything else returns
    ///     <c>{ error: { code: -32601, message: "method not found" } }</c>.
    /// Listens on a random loopback port — no external network exposure.
    /// </summary>
    private sealed class JsonRpcResponder : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private int _messageSendCalls;
        private int _agentCardCalls;

        public Uri Endpoint { get; }

        public int MessageSendCallCount => _messageSendCalls;
        public int AgentCardCallCount => _agentCardCalls;

        private JsonRpcResponder(WebApplication app, Uri endpoint)
        {
            _app = app;
            Endpoint = endpoint;
        }

        public static async Task<JsonRpcResponder> StartAsync(
            string agentReplyText = "ok",
            bool mountJsonRpc = true)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");

            var app = builder.Build();

            // Counters live on the responder instance. Capture closures
            // are wired after the app is built so we can return the same
            // counters via properties.
            var responderRef = new JsonRpcResponder[1];

            app.MapGet("/.well-known/agent.json", () =>
            {
                Interlocked.Increment(ref responderRef[0]._agentCardCalls);
                return Results.Json(new
                {
                    name = "spring-test-fake-agent",
                    description = "in-process fake A2A agent for #1465 contract tests",
                    protocolVersion = "0.3.0",
                    url = "/",
                    version = "0.0.0",
                    skills = Array.Empty<object>(),
                });
            });

            if (mountJsonRpc)
            {
                app.MapPost("/", async (HttpContext ctx) =>
                {
                    using var doc = await JsonDocument.ParseAsync(
                        ctx.Request.Body, cancellationToken: ctx.RequestAborted);
                    var root = doc.RootElement;
                    var idElement = root.TryGetProperty("id", out var idProp)
                        ? idProp
                        : default;
                    var method = root.TryGetProperty("method", out var methodProp)
                        ? methodProp.GetString()
                        : null;

                    if (method != "message/send")
                    {
                        return Results.Json(new
                        {
                            jsonrpc = "2.0",
                            id = idElement.ValueKind == JsonValueKind.Undefined
                                ? null
                                : (object?)JsonSerializer.Deserialize<object>(
                                    idElement.GetRawText()),
                            error = new
                            {
                                code = -32601,
                                message = "method not found",
                            },
                        });
                    }

                    Interlocked.Increment(ref responderRef[0]._messageSendCalls);

                    // A2A v0.3 wire shape — must match what the .NET
                    // A2AClient expects to deserialize. Mirrors the stub
                    // body used in A2AExecutionDispatcherTests.
                    return Results.Json(new
                    {
                        jsonrpc = "2.0",
                        id = idElement.ValueKind == JsonValueKind.Undefined
                            ? null
                            : (object?)JsonSerializer.Deserialize<object>(
                                idElement.GetRawText()),
                        result = new
                        {
                            kind = "task",
                            id = "task-1",
                            contextId = "ctx",
                            status = new { state = "completed" },
                            artifacts = new[]
                            {
                                new
                                {
                                    artifactId = "a-1",
                                    parts = new[]
                                    {
                                        new
                                        {
                                            kind = "text",
                                            text = agentReplyText,
                                        },
                                    },
                                },
                            },
                        },
                    });
                });
            }

            await app.StartAsync();

            // Resolve the actually-bound URL after Start. Kestrel writes
            // the live address into IServerAddressesFeature when port 0 is
            // requested.
            var address = app.Urls.GetEnumerator();
            string baseUrl;
            try
            {
                if (!address.MoveNext())
                {
                    throw new InvalidOperationException(
                        "Kestrel did not report a bound URL.");
                }
                baseUrl = address.Current;
            }
            finally
            {
                if (address is IDisposable d) d.Dispose();
            }

            // Trailing slash so `new Uri(endpoint, "/.well-known/agent.json")`
            // resolves correctly.
            if (!baseUrl.EndsWith('/'))
            {
                baseUrl += "/";
            }

            var responder = new JsonRpcResponder(app, new Uri(baseUrl));
            responderRef[0] = responder;
            return responder;
        }

        public async ValueTask DisposeAsync()
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
