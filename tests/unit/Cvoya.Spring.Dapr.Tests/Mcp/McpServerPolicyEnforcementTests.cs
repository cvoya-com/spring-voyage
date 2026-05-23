// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Mcp;

using System.Text.Json;

using Cvoya.Spring.Core.Messaging;
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
public class McpServerPolicyEnforcementTests
{
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly FakeEnforcer _enforcer = new();
    private readonly FakeRegistry _registry = new();
    private readonly McpServer _server;

    public McpServerPolicyEnforcementTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var services = new ServiceCollection();
        services.AddSingleton<IUnitPolicyEnforcer>(_enforcer);
        var provider = services.BuildServiceProvider();

        _server = new McpServer(
            [_registry],
            Options.Create(new McpServerOptions()),
            _loggerFactory,
            provider.GetRequiredService<IServiceScopeFactory>());
    }

    [Fact]
    public async Task ToolsCall_PolicyAllows_InvokesRegistry()
    {
        // IssueSession requires a Guid-shaped agentId + a subject scheme
        // (#2379 hardening — every session must carry a materialised
        // Subject for the effective-grant gate to evaluate). Hand in a
        // canonical no-dash Guid and assert through it instead of using
        // the legacy opaque "ada" id shape.
        var agentId = Guid.NewGuid().ToString("N");
        var session = _server!.IssueSession(agentId, "conv-1", Address.AgentScheme);
        _enforcer.NextDecision = PolicyDecision.Allowed;

        var json = await PostJsonAsync(session.Token, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "fake.tool", arguments = new { echo = "hi" } },
        });

        _registry.LastInvokedName.ShouldBe("fake.tool");
        _enforcer.LastAgentId.ShouldBe(agentId);
        _enforcer.LastToolName.ShouldBe("fake.tool");
        var result = json.GetProperty("result");
        result.TryGetProperty("isError", out var isError).ShouldBeTrue();
        isError.GetBoolean().ShouldBeFalse();
    }

    [Fact]
    public async Task ToolsCall_PolicyDenies_ShortCircuitsAsToolError()
    {
        var session = _server!.IssueSession(
            Guid.NewGuid().ToString("N"), "conv-1", Address.AgentScheme);
        _enforcer.NextDecision = PolicyDecision.Deny(
            "Tool 'fake.tool' is blocked by unit 'engineering' skill policy.",
            "engineering");

        var json = await PostJsonAsync(session.Token, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "fake.tool", arguments = new { echo = "hi" } },
        });

        _registry.LastInvokedName.ShouldBeNull();
        var result = json.GetProperty("result");
        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        text.ShouldContain("blocked");
        text.ShouldContain("engineering");
    }

    private Task<JsonElement> PostJsonAsync(string token, object body)
        => McpTestTransport.PostJsonAsync(
            _server, token, body, TestContext.Current.CancellationToken);

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

        public Task<PolicyDecision> EvaluateModelAsync(
            string agentId, string modelId, CancellationToken cancellationToken = default) =>
            Task.FromResult(PolicyDecision.Allowed);

        public Task<PolicyDecision> EvaluateCostAsync(
            string agentId, decimal projectedCost, CancellationToken cancellationToken = default) =>
            Task.FromResult(PolicyDecision.Allowed);

        public Task<PolicyDecision> EvaluateExecutionModeAsync(
            string agentId, Cvoya.Spring.Core.Agents.AgentExecutionMode mode, CancellationToken cancellationToken = default) =>
            Task.FromResult(PolicyDecision.Allowed);

        public Task<ExecutionModeResolution> ResolveExecutionModeAsync(
            string agentId, Cvoya.Spring.Core.Agents.AgentExecutionMode mode, CancellationToken cancellationToken = default) =>
            Task.FromResult(ExecutionModeResolution.AllowAsIs(mode));

        public Task<PolicyDecision> EvaluateInitiativeActionAsync(
            string agentId, string actionType, CancellationToken cancellationToken = default) =>
            Task.FromResult(PolicyDecision.Allowed);

        public Task<PolicyDecision> EvaluateUnitDirectoryReadAsync(
            string callerId, Guid targetUnitId, CancellationToken cancellationToken = default) =>
            Task.FromResult(PolicyDecision.Allowed);
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
            return [new ToolDefinition("fake.tool", "Fake echo tool.", schema, string.Empty)];
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
