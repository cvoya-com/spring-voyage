// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Mcp;

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Mcp;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// Tests that <see cref="McpServer"/> routes every <c>tools/call</c> through
/// the injected <see cref="IUnitPolicyEnforcer"/> and surfaces denials as
/// tool errors without invoking the registry.
/// </summary>
public class McpServerPolicyEnforcementTests : IAsyncLifetime
{
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly FakeEnforcer _enforcer = new();
    private readonly FakeRegistry _registry = new();
    private McpServer? _server;
    private HttpClient? _client;

    public McpServerPolicyEnforcementTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
    }

    public async ValueTask InitializeAsync()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IUnitPolicyEnforcer>(_enforcer);
        var provider = services.BuildServiceProvider();

        _server = new McpServer(
            [_registry],
            Options.Create(new McpServerOptions { ContainerHost = "127.0.0.1" }),
            _loggerFactory,
            provider.GetRequiredService<IServiceScopeFactory>());

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
    public async Task ToolsCall_PolicyAllows_InvokesRegistry()
    {
        var session = _server!.IssueSession("ada", "conv-1");
        _enforcer.NextDecision = PolicyDecision.Allowed;

        var json = await PostJsonAsync(session.Token, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "fake_tool", arguments = new { echo = "hi" } },
        });

        _registry.LastInvokedName.ShouldBe("fake_tool");
        _enforcer.LastAgentId.ShouldBe("ada");
        _enforcer.LastToolName.ShouldBe("fake_tool");
        var result = json.GetProperty("result");
        result.TryGetProperty("isError", out var isError).ShouldBeTrue();
        isError.GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task ToolsCall_PolicyDenies_ShortCircuitsAsToolError()
    {
        var session = _server!.IssueSession("ada", "conv-1");
        _enforcer.NextDecision = PolicyDecision.Deny(
            "Tool 'fake_tool' is blocked by unit 'engineering' skill policy.",
            "engineering");

        var json = await PostJsonAsync(session.Token, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "fake_tool", arguments = new { echo = "hi" } },
        });

        _registry.LastInvokedName.ShouldBeNull();
        var result = json.GetProperty("result");
        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        text.ShouldContain("blocked");
        text.ShouldContain("engineering");
    }

    private async Task<JsonElement> PostJsonAsync(string token, object body)
    {
        var json = JsonSerializer.Serialize(body);
        var request = new HttpRequestMessage(HttpMethod.Post, string.Empty)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _client!.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var content = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(content).RootElement.Clone();
    }

    private sealed class FakeEnforcer : IUnitPolicyEnforcer
    {
        public PolicyDecision NextDecision { get; set; } = PolicyDecision.Allowed;
        public string? LastAgentId { get; private set; }
        public string? LastToolName { get; private set; }

        public Task<PolicyDecision> EvaluateSkillInvocationAsync(
            string agentId, string toolName, CancellationToken cancellationToken = default)
        {
            LastAgentId = agentId;
            LastToolName = toolName;
            return Task.FromResult(NextDecision);
        }
    }

    private sealed class FakeRegistry : ISkillRegistry
    {
        public string Name => "fake";
        public string? LastInvokedName { get; private set; }

        public IReadOnlyList<ToolDefinition> GetToolDefinitions()
        {
            var schema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { echo = new { type = "string" } },
            });
            return [new ToolDefinition("fake_tool", "Fake echo tool.", schema)];
        }

        public Task<JsonElement> InvokeAsync(
            string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            LastInvokedName = toolName;
            var result = new
            {
                echo = arguments.TryGetProperty("echo", out var e) ? e.GetString() : null,
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(result));
        }
    }
}