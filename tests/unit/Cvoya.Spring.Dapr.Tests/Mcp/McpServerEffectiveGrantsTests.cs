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
/// Tests that <see cref="McpServer"/> uses <see cref="IToolGrantResolver"/>
/// as the runtime authorization gate for <c>tools/list</c> and
/// <c>tools/call</c> (#2379). Registration tells the server "this tool
/// exists"; the resolver tells it "this subject may see/invoke it"; unit
/// policy remains a deny overlay applied after the grant gate.
/// <para>
/// ADR-0052 / Wave 3 (#2625): the MCP surface is a minimal-API route on the
/// worker's Kestrel host — these tests drive
/// <see cref="McpServer.HandleRequestAsync"/> through
/// <see cref="McpTestTransport"/>.
/// </para>
/// </summary>
public class McpServerEffectiveGrantsTests
{
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly FakeToolGrantResolver _resolver = new();
    private readonly FakeRegistry _registry = new(
        ("sv.directory.get_self", "sv"),
        ("acme.create_issue", "acme"),
        ("arxiv.search", "arxiv"));
    private readonly FakeEnforcer _enforcer = new();
    private readonly McpServer _server;

    public McpServerEffectiveGrantsTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());

        var services = new ServiceCollection();
        services.AddSingleton<IToolGrantResolver>(_resolver);
        services.AddSingleton<IUnitPolicyEnforcer>(_enforcer);
        var provider = services.BuildServiceProvider();

        _server = new McpServer(
            [_registry],
            Options.Create(new McpServerOptions()),
            _loggerFactory,
            provider.GetRequiredService<IServiceScopeFactory>());
    }

    [Fact]
    public async Task ToolsList_UnboundConnectorTool_AbsentFromResult()
    {
        // The agent has only the implicit sv.* platform tools — the
        // connector and arxiv namespaces are registered but not granted.
        // tools/list must return only what the resolver hands back.
        var agentId = Guid.NewGuid();
        _resolver.SetGrants(
            new Address(Address.AgentScheme, agentId),
            new EffectiveTool("sv.directory.get_self", "sv", "Get self.", ToolProvenance.Platform, null));

        var session = _server!.IssueSession(agentId.ToString("N"), "conv-1", Address.AgentScheme);
        var json = await PostJsonAsync(session.Token, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/list",
        });

        var tools = json.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        tools.ShouldBe(new[] { "sv.directory.get_self" });
        tools.ShouldNotContain("acme.create_issue");
        tools.ShouldNotContain("arxiv.search");
    }

    [Fact]
    public async Task ToolsCall_UnboundConnectorTool_RejectedWithToolNotGrantedError()
    {
        // Same setup as above — registry knows acme.create_issue, but
        // the resolver does not surface it. tools/call must reject before
        // the registry sees the request.
        var agentId = Guid.NewGuid();
        _resolver.SetGrants(
            new Address(Address.AgentScheme, agentId),
            new EffectiveTool("sv.directory.get_self", "sv", "Get self.", ToolProvenance.Platform, null));

        var session = _server!.IssueSession(agentId.ToString("N"), "conv-1", Address.AgentScheme);
        var json = await PostJsonAsync(session.Token, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "acme.create_issue", arguments = new { } },
        });

        // Structured JSON-RPC error with the dedicated ToolNotGranted code
        // (-32002) — distinct from MethodNotFound so the caller can tell
        // "the server doesn't know that tool" apart from "the caller is
        // not authorised for it".
        json.TryGetProperty("error", out var error).ShouldBeTrue();
        error.GetProperty("code").GetInt32().ShouldBe(McpRpcErrorCodes.ToolNotGranted);
        error.GetProperty("message").GetString()!.ShouldContain("acme.create_issue");

        _registry.LastInvokedName.ShouldBeNull();
        _enforcer.LastToolName.ShouldBeNull();
    }

    [Fact]
    public async Task ToolsList_BoundConnectorTool_PresentWhenGranted()
    {
        // The agent has an inherited connector grant for acme.* — the
        // resolver returns it, and tools/list surfaces it.
        var agentId = Guid.NewGuid();
        _resolver.SetGrants(
            new Address(Address.AgentScheme, agentId),
            new EffectiveTool("sv.directory.get_self", "sv", "Get self.", ToolProvenance.Platform, null),
            new EffectiveTool(
                "acme.create_issue",
                "acme",
                "Create an issue.",
                ToolProvenance.ConnectorPrefix + "acme",
                InheritedFromUnitName: "Engineering"));

        var session = _server!.IssueSession(agentId.ToString("N"), "conv-1", Address.AgentScheme);
        var json = await PostJsonAsync(session.Token, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/list",
        });

        var tools = json.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
        tools.ShouldBe(new[] { "acme.create_issue", "sv.directory.get_self" });
    }

    [Fact]
    public async Task ToolsList_PlatformTool_PresentWithoutExplicitRow()
    {
        // The resolver's contract is that sv.* tools are implicit — they
        // surface from the platform tier without any per-subject row. We
        // simulate that here by handing the resolver only platform-tier
        // entries; the server must not require additional rows to surface
        // them in tools/list.
        var agentId = Guid.NewGuid();
        _resolver.SetGrants(
            new Address(Address.AgentScheme, agentId),
            new EffectiveTool("sv.directory.get_self", "sv", "Get self.", ToolProvenance.Platform, null));

        var session = _server!.IssueSession(agentId.ToString("N"), "conv-1", Address.AgentScheme);
        var json = await PostJsonAsync(session.Token, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/list",
        });

        var tools = json.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        tools.ShouldContain("sv.directory.get_self");
    }

    [Fact]
    public async Task ToolsCall_GrantedButPolicyDenies_RejectedByPolicyAfterGrantPasses()
    {
        // The second-gate ordering must be preserved: even when the tool
        // is in the effective grant set, unit policy can still deny. The
        // grant gate runs first (no -32002 error), then the policy
        // enforcer (returns isError=true success result).
        var agentId = Guid.NewGuid();
        _resolver.SetGrants(
            new Address(Address.AgentScheme, agentId),
            new EffectiveTool(
                "acme.create_issue",
                "acme",
                "Create an issue.",
                ToolProvenance.ConnectorPrefix + "acme",
                null));
        _enforcer.NextDecision = PolicyDecision.Deny(
            "Tool 'acme.create_issue' is blocked by unit 'engineering' skill policy.",
            "engineering");

        var session = _server!.IssueSession(agentId.ToString("N"), "conv-1", Address.AgentScheme);
        var json = await PostJsonAsync(session.Token, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "acme.create_issue", arguments = new { } },
        });

        // No -32002 error; policy fired and short-circuited as isError=true.
        json.TryGetProperty("error", out _).ShouldBeFalse();
        var result = json.GetProperty("result");
        result.GetProperty("isError").GetBoolean().ShouldBeTrue();
        result.GetProperty("content")[0].GetProperty("text").GetString()!.ShouldContain("blocked");

        _enforcer.LastToolName.ShouldBe("acme.create_issue");
        _registry.LastInvokedName.ShouldBeNull();
    }

    [Fact]
    public async Task ToolsCall_UnitSubject_RoutesThroughResolverForUnitScheme()
    {
        // The session can be issued for a unit caller as well as an agent.
        // Materialising the subject from (agentId, callerKind) means the
        // resolver must be consulted with a unit:<guid> address — the
        // resolver's contract supports both schemes.
        var unitId = Guid.NewGuid();
        _resolver.SetGrants(
            new Address(Address.UnitScheme, unitId),
            new EffectiveTool("arxiv.search", "arxiv", "Search Arxiv.", ToolProvenance.ConnectorPrefix + "arxiv", null));

        var session = _server!.IssueSession(unitId.ToString("N"), "conv-1", Address.UnitScheme);
        var json = await PostJsonAsync(session.Token, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/list",
        });

        var tools = json.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString())
            .ToList();
        tools.ShouldBe(new[] { "arxiv.search" });
    }

    [Fact]
    public async Task ToolsCall_ResolverThrows_SurfacesAsInternalErrorRatherThanAllowAll()
    {
        // Regression for the #2379 cleanup: an earlier draft of the gate
        // swallowed resolver exceptions and degraded to allow-all so a
        // transient datastore outage didn't fail every tool call. That
        // fail-open would let any subject invoke any registered tool the
        // moment the resolver hiccups, so the gate now propagates the
        // failure as a JSON-RPC InternalError (-32603) and the registry
        // is never invoked.
        var agentId = Guid.NewGuid();
        _resolver.NextException = new InvalidOperationException("resolver offline");

        var session = _server!.IssueSession(agentId.ToString("N"), "conv-1", Address.AgentScheme);
        var json = await PostJsonAsync(session.Token, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "acme.create_issue", arguments = new { } },
        });

        json.TryGetProperty("error", out var error).ShouldBeTrue();
        error.GetProperty("code").GetInt32().ShouldBe(McpRpcErrorCodes.InternalError);
        _registry.LastInvokedName.ShouldBeNull();
        _enforcer.LastToolName.ShouldBeNull();
    }

    [Fact]
    public void IssueSession_NonGuidAgentId_ThrowsRatherThanIssuingBypassSession()
    {
        // Regression for the #2379 cleanup: an earlier draft accepted any
        // agentId and left Subject null when the id wasn't Guid-shaped,
        // silently bypassing the effective-grant gate for that session.
        // The contract now requires every session to carry a materialised
        // Subject so the gate applies uniformly — opaque ids fail fast.
        var act = () => _server!.IssueSession("ada", "conv-1", Address.AgentScheme);

        Should.Throw<ArgumentException>(act).ParamName.ShouldBe("agentId");
    }

    [Fact]
    public void IssueSession_NonSubjectCallerKind_ThrowsRatherThanIssuingBypassSession()
    {
        // Only agent: / unit: subjects are routable through IToolGrantResolver.
        // Connector / human / unknown schemes are rejected at session-issue
        // time rather than being silently allow-listed past the gate.
        var act = () => _server!.IssueSession(
            Guid.NewGuid().ToString("N"), "conv-1", Address.HumanScheme);

        Should.Throw<ArgumentException>(act).ParamName.ShouldBe("callerKind");
    }

    [Fact]
    public async Task ToolsCall_GrantedAndPolicyAllows_InvokesRegistry()
    {
        // Happy path — confirms the resolver isn't blocking grants that
        // should pass. Both gates allow; the registry sees the call.
        var agentId = Guid.NewGuid();
        _resolver.SetGrants(
            new Address(Address.AgentScheme, agentId),
            new EffectiveTool(
                "acme.create_issue",
                "acme",
                "Create an issue.",
                ToolProvenance.ConnectorPrefix + "acme",
                null));
        _enforcer.NextDecision = PolicyDecision.Allowed;

        var session = _server!.IssueSession(agentId.ToString("N"), "conv-1", Address.AgentScheme);
        var json = await PostJsonAsync(session.Token, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "acme.create_issue", arguments = new { echo = "hi" } },
        });

        json.TryGetProperty("error", out _).ShouldBeFalse();
        json.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeFalse();
        _registry.LastInvokedName.ShouldBe("acme.create_issue");
    }

    private Task<JsonElement> PostJsonAsync(string token, object body)
        => McpTestTransport.PostJsonAsync(
            _server, token, body, TestContext.Current.CancellationToken);

    private sealed class FakeToolGrantResolver : IToolGrantResolver
    {
        private readonly Dictionary<Address, IReadOnlyList<EffectiveTool>> _grants = new();

        public Exception? NextException { get; set; }

        public void SetGrants(Address subject, params EffectiveTool[] tools)
        {
            _grants[subject] = tools;
        }

        public Task<IReadOnlyList<EffectiveTool>> ResolveAsync(
            Address subject, CancellationToken cancellationToken = default)
        {
            if (NextException is not null)
            {
                throw NextException;
            }
            if (_grants.TryGetValue(subject, out var tools))
            {
                return Task.FromResult(tools);
            }
            return Task.FromResult<IReadOnlyList<EffectiveTool>>(Array.Empty<EffectiveTool>());
        }
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
        private readonly IReadOnlyList<(string Name, string Namespace)> _tools;

        public FakeRegistry(params (string Name, string Namespace)[] tools)
        {
            _tools = tools;
        }

        public string Name => "fake";
        public string? LastInvokedName { get; private set; }

        public IReadOnlyList<ToolDefinition> GetToolDefinitions()
        {
            var schema = JsonSerializer.SerializeToElement(new
            {
                type = "object",
                properties = new { echo = new { type = "string" } },
            });
            return _tools.Select(t => new ToolDefinition(t.Name, $"desc({t.Name})", schema)).ToList();
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
