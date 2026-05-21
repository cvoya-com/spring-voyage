// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Host.Api.Tests.Endpoints;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Orchestration;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Covers the MCP JSON-RPC 2.0 handler at the messaging route-prefix root
/// (<c>POST /v1/runtime/orchestration</c>) — the streamable-HTTP transport the
/// claude-code / codex launchers point their MCP server at. The REST
/// sub-routes (<c>/messaging/send</c>, <c>/messaging/broadcast</c>) are
/// exercised separately by <see cref="OrchestrationCallbackEndpointsTests"/>.
/// </summary>
public class OrchestrationMcpEndpointTests
{
    private const string McpRoute = "/v1/runtime/orchestration";

    private static readonly Address UnitAddress =
        new(Address.UnitScheme, Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"));

    private static readonly Address ChildAddress =
        new(Address.AgentScheme, Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"));

    private static readonly Address OtherChildAddress =
        new(Address.AgentScheme, Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002"));

    [Fact]
    public async Task Initialize_ReturnsServerInfoAndCapabilities()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            McpRoute,
            Rpc("initialize"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await ReadJsonAsync(response);
        json.GetProperty("jsonrpc").GetString().ShouldBe("2.0");
        var result = json.GetProperty("result");
        result.GetProperty("protocolVersion").GetString().ShouldBe("2024-11-05");
        result.GetProperty("serverInfo").GetProperty("name").GetString().ShouldBe("spring-messaging");
        result.GetProperty("capabilities").TryGetProperty("tools", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Initialize_TrailingSlashRouteAlsoMatches()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            McpRoute + "/",
            Rpc("initialize"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ToolsList_ReturnsBothToolsWithInputSchemas()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            McpRoute,
            Rpc("tools/list"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await ReadJsonAsync(response);
        var tools = json.GetProperty("result").GetProperty("tools");
        tools.GetArrayLength().ShouldBe(2);

        var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToArray();
        names.ShouldContain("sv.messaging.send");
        names.ShouldContain("sv.messaging.broadcast");

        foreach (var tool in tools.EnumerateArray())
        {
            tool.GetProperty("description").GetString().ShouldNotBeNullOrWhiteSpace();
            tool.GetProperty("inputSchema").GetProperty("type").GetString().ShouldBe("object");
        }
    }

    [Fact]
    public async Task ToolsCall_Send_DeliversMessageAndReturnsDeliveryAck()
    {
        // ADR-0049 — tools/call sv.messaging.send returns a delivery
        // acknowledgement in the MCP envelope, never the target's response.
        using var factory = new OrchestrationCallbackTestHost();
        var agent = Substitute.For<IAgent>();
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        factory.RegisterAgent(ChildAddress, agent);

        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            McpRoute,
            Rpc("tools/call", new
            {
                name = "sv.messaging.send",
                arguments = new
                {
                    address = ChildAddress.ToString(),
                    message = "do the work",
                    reason = "because",
                },
            }),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await ReadJsonAsync(response);
        var result = json.GetProperty("result");
        result.GetProperty("isError").GetBoolean().ShouldBeFalse();

        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        var inner = JsonSerializer.Deserialize<JsonElement>(text!);
        inner.GetProperty("delivered").GetBoolean().ShouldBeTrue();
        inner.GetProperty("target").GetString().ShouldBe(ChildAddress.ToString());
    }

    [Fact]
    public async Task ToolsCall_Broadcast_ReturnsPerTargetDeliveryOutcomes()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var firstAgent = Substitute.For<IAgent>();
        var secondAgent = Substitute.For<IAgent>();
        firstAgent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        secondAgent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns((Message?)null);
        factory.RegisterAgent(ChildAddress, firstAgent);
        factory.RegisterAgent(OtherChildAddress, secondAgent);

        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            McpRoute,
            Rpc("tools/call", new
            {
                name = "sv.messaging.broadcast",
                arguments = new
                {
                    addresses = new[] { ChildAddress.ToString(), OtherChildAddress.ToString() },
                    message = "parallel work",
                    reason = "fan it out",
                },
            }),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await ReadJsonAsync(response);
        var result = json.GetProperty("result");
        result.GetProperty("isError").GetBoolean().ShouldBeFalse();

        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        var inner = JsonSerializer.Deserialize<JsonElement>(text!);
        var deliveries = inner.GetProperty("deliveries");
        deliveries.GetArrayLength().ShouldBe(2);
        deliveries[0].GetProperty("delivered").GetBoolean().ShouldBeTrue();
        deliveries[1].GetProperty("delivered").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ToolsCall_Send_SelfTarget_ReturnsIsErrorResult()
    {
        // OrchestrationException (self-delegation) is surfaced to the model as
        // a tools/call result with isError: true — the JSON-RPC call itself
        // succeeded, so it is HTTP 200, not a transport error.
        using var factory = new OrchestrationCallbackTestHost();
        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            McpRoute,
            Rpc("tools/call", new
            {
                name = "sv.messaging.send",
                arguments = new
                {
                    address = UnitAddress.ToString(),
                    message = "loop back to myself",
                    reason = (string?)null,
                },
            }),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await ReadJsonAsync(response);
        var result = json.GetProperty("result");
        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        text.ShouldNotBeNull();
        text.ShouldContain(OrchestrationException.RejectCodes.OrchestrationSelfDelegation);
    }

    [Fact]
    public async Task ToolsCall_Send_MalformedAddress_ReturnsIsErrorResult()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            McpRoute,
            Rpc("tools/call", new
            {
                name = "sv.messaging.send",
                arguments = new { address = "not-an-address", message = "x" },
            }),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await ReadJsonAsync(response);
        json.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ToolsCall_Send_TerminalDeliveryFailure_ReturnsIsErrorResult()
    {
        // ADR-0049 §6 — a transient ReceiveAsync failure that persists past
        // the retry budget surfaces to the model as an isError tools/call
        // result carrying the OrchestrationDeliveryFailed reject code.
        using var factory = new OrchestrationCallbackTestHost();
        var agent = Substitute.For<IAgent>();
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns<Message?>(_ => throw new InvalidOperationException("dapr is down"));
        factory.RegisterAgent(ChildAddress, agent);

        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            McpRoute,
            Rpc("tools/call", new
            {
                name = "sv.messaging.send",
                arguments = new { address = ChildAddress.ToString(), message = "do the work" },
            }),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await ReadJsonAsync(response);
        var result = json.GetProperty("result");
        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        text.ShouldNotBeNull();
        text.ShouldContain(OrchestrationException.RejectCodes.OrchestrationDeliveryFailed);
    }

    [Fact]
    public async Task UnknownMethod_ReturnsJsonRpcMethodNotFound()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            McpRoute,
            Rpc("resources/list"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await ReadJsonAsync(response);
        json.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32601);
    }

    [Fact]
    public async Task MissingAuthHeader_Returns401WithJsonRpcErrorEnvelope()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            McpRoute,
            Rpc("initialize"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var json = await ReadJsonAsync(response);
        json.GetProperty("jsonrpc").GetString().ShouldBe("2.0");
        json.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32001);
        // Must NOT be the REST {error, message} shape.
        json.TryGetProperty("message", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task InvalidToken_Returns401WithJsonRpcErrorEnvelope()
    {
        using var factory = new OrchestrationCallbackTestHost();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not-a-jwt");

        var response = await client.PostAsJsonAsync(
            McpRoute,
            Rpc("initialize"),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var json = await ReadJsonAsync(response);
        json.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32001);
    }

    private static object Rpc(string method, object? @params = null) =>
        @params is null
            ? new { jsonrpc = "2.0", id = 1, method }
            : new { jsonrpc = "2.0", id = 1, method, @params };

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
}
