// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Mcp;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Mcp;

using FluentAssertions;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Xunit;

/// <summary>
/// End-to-end tests for <see cref="McpServer"/>. Boots the server on a real
/// loopback HTTP listener and exercises it via <see cref="HttpClient"/>.
/// </summary>
public class McpServerTests : IAsyncLifetime
{
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly FakeSkillRegistry _registry = new();
    private McpServer? _server;
    private HttpClient? _client;

    public McpServerTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
    }

    public async ValueTask InitializeAsync()
    {
        _server = new McpServer(
            [_registry],
            Options.Create(new McpServerOptions { ContainerHost = "127.0.0.1" }),
            _loggerFactory);
        await _server.StartAsync(CancellationToken.None);
        _client = new HttpClient { BaseAddress = new Uri(_server.Endpoint!) };
    }

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.StopAsync(CancellationToken.None);
            _server.Dispose();
        }
        _client?.Dispose();
    }

    [Fact]
    public async Task MissingToken_ReturnsUnauthorized()
    {
        var response = await PostAsync(token: null, new { jsonrpc = "2.0", id = 1, method = "initialize" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Initialize_ReturnsServerInfoAndBoundSession()
    {
        var session = _server!.IssueSession("agent-1", "conv-1");

        var json = await PostJsonAsync(session.Token, new { jsonrpc = "2.0", id = 1, method = "initialize" });

        var result = json.GetProperty("result");
        result.GetProperty("serverInfo").GetProperty("name").GetString().Should().Be("spring-voyage-mcp");
        result.GetProperty("meta").GetProperty("agentId").GetString().Should().Be("agent-1");
        result.GetProperty("meta").GetProperty("conversationId").GetString().Should().Be("conv-1");
    }

    [Fact]
    public async Task ToolsList_ReturnsAllToolsAcrossRegistries()
    {
        var session = _server!.IssueSession("a", "c");

        var json = await PostJsonAsync(session.Token, new { jsonrpc = "2.0", id = 1, method = "tools/list" });

        var tools = json.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        tools.Should().HaveCount(1);
        tools[0].GetProperty("name").GetString().Should().Be("fake_tool");
    }

    [Fact]
    public async Task ToolsCall_RoutesToCorrectRegistryAndReturnsResult()
    {
        var session = _server!.IssueSession("a", "c");

        var json = await PostJsonAsync(session.Token, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "fake_tool",
                arguments = new { echo = "hello" }
            }
        });

        _registry.LastInvokedName.Should().Be("fake_tool");
        var content = json.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        JsonDocument.Parse(content).RootElement.GetProperty("echo").GetString().Should().Be("hello");
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_ReturnsMethodNotFound()
    {
        var session = _server!.IssueSession("a", "c");

        var json = await PostJsonAsync(session.Token, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "nope", arguments = new { } }
        });

        json.GetProperty("error").GetProperty("code").GetInt32().Should().Be(-32601);
    }

    [Fact]
    public async Task RevokedToken_ReturnsUnauthorized()
    {
        var session = _server!.IssueSession("a", "c");
        _server.RevokeSession(session.Token);

        var response = await PostAsync(
            session.Token,
            new { jsonrpc = "2.0", id = 1, method = "initialize" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public void DuplicateToolRegistration_ThrowsAtConstruction()
    {
        var dup1 = new FakeSkillRegistry("dup");
        var dup2 = new FakeSkillRegistry("dup");

        var act = () => new McpServer(
            [dup1, dup2],
            Options.Create(new McpServerOptions()),
            _loggerFactory);

        act.Should().Throw<SpringException>().WithMessage("*more than one ISkillRegistry*");
    }

    private async Task<HttpResponseMessage> PostAsync(string? token, object body)
    {
        var json = JsonSerializer.Serialize(body);
        var request = new HttpRequestMessage(HttpMethod.Post, string.Empty)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return await _client!.SendAsync(request);
    }

    private async Task<JsonElement> PostJsonAsync(string token, object body)
    {
        using var response = await PostAsync(token, body);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content).RootElement.Clone();
    }

    private sealed class FakeSkillRegistry : ISkillRegistry
    {
        private readonly string _toolName;

        public FakeSkillRegistry(string toolName = "fake_tool")
        {
            _toolName = toolName;
        }

        public string Name => "fake";
        public string? LastInvokedName { get; private set; }

        public IReadOnlyList<ToolDefinition> GetToolDefinitions()
        {
            var schema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { echo = new { type = "string" } }
            });
            return [new ToolDefinition(_toolName, "Fake echo tool.", schema)];
        }

        public Task<JsonElement> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            LastInvokedName = toolName;
            var result = new
            {
                echo = arguments.TryGetProperty("echo", out var e) ? e.GetString() : null
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(result));
        }
    }
}