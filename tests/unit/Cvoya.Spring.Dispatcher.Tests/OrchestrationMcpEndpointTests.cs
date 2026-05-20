// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dispatcher.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Runtime;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.Orchestration;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dispatcher.Auth;

using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Covers the MCP JSON-RPC 2.0 handler at the orchestration route-prefix root
/// (<c>POST /v1/runtime/orchestration</c>) — the streamable-HTTP transport the
/// claude-code / codex launchers point their <c>spring-orchestration</c> MCP
/// server at. The REST sub-routes (<c>/delegate-to</c>, <c>/fanout-to</c>) are
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
        using var factory = new OrchestrationDispatcherFactory();
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
        result.GetProperty("serverInfo").GetProperty("name").GetString().ShouldBe("spring-orchestration");
        result.GetProperty("capabilities").TryGetProperty("tools", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Initialize_TrailingSlashRouteAlsoMatches()
    {
        using var factory = new OrchestrationDispatcherFactory();
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
        using var factory = new OrchestrationDispatcherFactory();
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
        names.ShouldContain("delegate_to");
        names.ShouldContain("fanout_to");

        foreach (var tool in tools.EnumerateArray())
        {
            tool.GetProperty("description").GetString().ShouldNotBeNullOrWhiteSpace();
            tool.GetProperty("inputSchema").GetProperty("type").GetString().ShouldBe("object");
        }
    }

    [Fact]
    public async Task ToolsCall_DelegateTo_RoutesMessageAndReturnsResponse()
    {
        using var factory = new OrchestrationDispatcherFactory();
        var agent = Substitute.For<IAgent>();
        agent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(CreateResponse(ChildAddress, UnitAddress));
        factory.RegisterAgent(ChildAddress, agent);

        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            McpRoute,
            Rpc("tools/call", new
            {
                name = "delegate_to",
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
        inner.GetProperty("messageContent").GetString().ShouldBe("done");
    }

    [Fact]
    public async Task ToolsCall_FanoutTo_ReturnsPerTargetResults()
    {
        using var factory = new OrchestrationDispatcherFactory();
        var firstAgent = Substitute.For<IAgent>();
        var secondAgent = Substitute.For<IAgent>();
        firstAgent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(CreateResponse(ChildAddress, UnitAddress));
        secondAgent.ReceiveAsync(Arg.Any<Message>(), Arg.Any<CancellationToken>())
            .Returns(CreateResponse(OtherChildAddress, UnitAddress, "also done"));
        factory.RegisterAgent(ChildAddress, firstAgent);
        factory.RegisterAgent(OtherChildAddress, secondAgent);

        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            McpRoute,
            Rpc("tools/call", new
            {
                name = "fanout_to",
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
        inner.GetArrayLength().ShouldBe(2);
        inner[0].GetProperty("success").GetBoolean().ShouldBeTrue();
        inner[1].GetProperty("success").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task ToolsCall_DelegateTo_SelfTarget_ReturnsIsErrorResult()
    {
        // OrchestrationException (self-delegation) is surfaced to the model as
        // a tools/call result with isError: true — the JSON-RPC call itself
        // succeeded, so it is HTTP 200, not a transport error.
        using var factory = new OrchestrationDispatcherFactory();
        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            McpRoute,
            Rpc("tools/call", new
            {
                name = "delegate_to",
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
    public async Task ToolsCall_DelegateTo_MalformedAddress_ReturnsIsErrorResult()
    {
        using var factory = new OrchestrationDispatcherFactory();
        var client = factory.CreateCallbackClient(UnitAddress);

        var response = await client.PostAsJsonAsync(
            McpRoute,
            Rpc("tools/call", new
            {
                name = "delegate_to",
                arguments = new { address = "not-an-address", message = "x" },
            }),
            TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await ReadJsonAsync(response);
        json.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeTrue();
    }

    [Fact]
    public async Task UnknownMethod_ReturnsJsonRpcMethodNotFound()
    {
        using var factory = new OrchestrationDispatcherFactory();
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
        using var factory = new OrchestrationDispatcherFactory();
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
        using var factory = new OrchestrationDispatcherFactory();
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

    private static Message CreateResponse(Address from, Address to, string content = "done") =>
        new(
            Guid.NewGuid(),
            from,
            to,
            MessageType.Domain,
            Guid.NewGuid().ToString("N"),
            JsonSerializer.SerializeToElement(new { content }),
            DateTimeOffset.UtcNow);

    private sealed class OrchestrationDispatcherFactory : DispatcherWebApplicationFactory
    {
        private static readonly byte[] SigningKey =
        [
            0x30, 0x9d, 0xe3, 0xf8, 0x4d, 0x02, 0x5d, 0xaf,
            0x76, 0x11, 0xc8, 0x96, 0x4e, 0x61, 0x73, 0x0b,
            0x44, 0x8e, 0x26, 0x74, 0x95, 0xe2, 0xab, 0x19,
            0xda, 0xc4, 0x31, 0x82, 0x07, 0xbd, 0x58, 0x6f,
        ];

        private readonly Dictionary<string, IAgent> _agents = new();
        private readonly ITenantSigningKeyProvider _keyProvider;

        public OrchestrationDispatcherFactory()
        {
            _keyProvider = Substitute.For<ITenantSigningKeyProvider>();
            _keyProvider.GetSigningKey(Arg.Any<Guid>()).Returns(SigningKey);
        }

        public Guid TenantId { get; } = Guid.Parse("dd55c4ea-8d72-5e43-a9df-88d07af02b69");

        public Guid ThreadId { get; } = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");

        public void RegisterAgent(Address address, IAgent agent) =>
            _agents[$"{address.Scheme}:{address.Id:N}"] = agent;

        public HttpClient CreateCallbackClient(Address caller)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", IssueToken(caller));
            return client;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IAgentProxyResolver>();
                services.RemoveAll<ITenantSigningKeyProvider>();

                services.AddSingleton(CreateAgentProxyResolver());
                services.AddSingleton(_keyProvider);
            });
        }

        private string IssueToken(Address caller)
        {
            var issuer = new CallbackTokenIssuer(
                _keyProvider,
                Options.Create(new CallbackTokenOptions()));

            return issuer.Issue(new CallbackToken(
                TenantId,
                caller,
                ThreadId,
                Guid.NewGuid(),
                ExpiresAt: default));
        }

        private IAgentProxyResolver CreateAgentProxyResolver()
        {
            var resolver = Substitute.For<IAgentProxyResolver>();
            resolver.Resolve(Arg.Any<string>(), Arg.Any<string>())
                .Returns(ci =>
                {
                    var scheme = ci.ArgAt<string>(0);
                    var actorId = ci.ArgAt<string>(1);
                    return _agents.TryGetValue($"{scheme}:{actorId}", out var agent)
                        ? agent
                        : null;
                });

            return resolver;
        }
    }
}
