// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Mcp;

using System.Text.Json;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Mcp;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// End-to-end tests for <see cref="McpServer"/>. ADR-0052 / Wave 3 (#2625):
/// the MCP surface is a minimal-API route on the worker's Kestrel host —
/// these tests drive <see cref="McpServer.HandleRequestAsync"/> directly
/// through an in-memory <see cref="Microsoft.AspNetCore.Http.DefaultHttpContext"/>
/// (see <see cref="McpTestTransport"/>), exercising the same JSON-RPC dispatch
/// the production route exercises without binding a socket.
/// </summary>
public class McpServerTests
{
    private readonly ILoggerFactory _loggerFactory = Substitute.For<ILoggerFactory>();
    private readonly FakeSkillRegistry _registry = new();
    private readonly McpServer _server;

    public McpServerTests()
    {
        _loggerFactory.CreateLogger(Arg.Any<string>()).Returns(Substitute.For<ILogger>());
        _server = new McpServer(
            [_registry],
            Options.Create(new McpServerOptions()),
            _loggerFactory);
    }

    [Fact]
    public async Task MissingToken_ReturnsUnauthorized()
    {
        var (statusCode, _) = await McpTestTransport.PostAsync(
            _server, token: null, new { jsonrpc = "2.0", id = 1, method = "initialize" },
            TestContext.Current.CancellationToken);

        statusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task Initialize_ReturnsServerInfoAndBoundSession()
    {
        // IssueSession requires a Guid-shaped agentId + a subject-scheme
        // callerKind (#2379 hardening) so the session's Subject is always
        // present and the effective-grant gate applies uniformly. We hand
        // in canonical no-dash Guids here and assert against them.
        var agentId = Guid.NewGuid().ToString("N");
        var threadId = Guid.NewGuid().ToString("N");
        var session = _server.IssueSession(agentId, threadId, Address.AgentScheme);

        var json = await McpTestTransport.PostJsonAsync(
            _server, session.Token, new { jsonrpc = "2.0", id = 1, method = "initialize" },
            TestContext.Current.CancellationToken);

        var result = json.GetProperty("result");
        result.GetProperty("serverInfo").GetProperty("name").GetString().ShouldBe("spring-voyage-mcp");
        result.GetProperty("meta").GetProperty("agentId").GetString().ShouldBe(agentId);
        result.GetProperty("meta").GetProperty("threadId").GetString().ShouldBe(threadId);
    }

    [Fact]
    public async Task ToolsList_ReturnsAllToolsAcrossRegistries()
    {
        // No IToolGrantResolver is registered in this fixture, so the
        // server returns the unfiltered registry surface — exactly the
        // shape exercised here.
        var session = _server.IssueSession(Guid.NewGuid().ToString("N"), "conv-1", Address.AgentScheme);

        var json = await McpTestTransport.PostJsonAsync(
            _server, session.Token, new { jsonrpc = "2.0", id = 1, method = "tools/list" },
            TestContext.Current.CancellationToken);

        var tools = json.GetProperty("result").GetProperty("tools").EnumerateArray().ToList();
        tools.Count.ShouldBe(1);
        tools[0].GetProperty("name").GetString().ShouldBe("fake.tool");
    }

    [Fact]
    public async Task ToolsCall_RoutesToCorrectRegistryAndReturnsResult()
    {
        var session = _server.IssueSession(Guid.NewGuid().ToString("N"), "conv-1", Address.AgentScheme);

        var json = await McpTestTransport.PostJsonAsync(_server, session.Token, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new
            {
                name = "fake.tool",
                arguments = new { echo = "hello" }
            }
        }, TestContext.Current.CancellationToken);

        _registry.LastInvokedName.ShouldBe("fake.tool");
        var content = json.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        JsonDocument.Parse(content).RootElement.GetProperty("echo").GetString().ShouldBe("hello");
    }

    [Fact]
    public async Task ToolsCall_UnknownTool_ReturnsMethodNotFound()
    {
        var session = _server.IssueSession(Guid.NewGuid().ToString("N"), "conv-1", Address.AgentScheme);

        var json = await McpTestTransport.PostJsonAsync(_server, session.Token, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "nope", arguments = new { } }
        }, TestContext.Current.CancellationToken);

        json.GetProperty("error").GetProperty("code").GetInt32().ShouldBe(-32601);
    }

    [Fact]
    public async Task RevokedToken_ReturnsUnauthorized()
    {
        var session = _server.IssueSession(Guid.NewGuid().ToString("N"), "conv-1", Address.AgentScheme);
        _server.RevokeSession(session.Token);

        var (statusCode, _) = await McpTestTransport.PostAsync(
            _server,
            session.Token,
            new { jsonrpc = "2.0", id = 1, method = "initialize" },
            TestContext.Current.CancellationToken);

        statusCode.ShouldBe(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public void IssueSession_NonGuidAgentId_ThrowsArgumentException()
    {
        // #2379 hardening: every session must carry a materialised Subject
        // so the effective-grant gate can evaluate it. An opaque id like
        // "ada" is rejected at session-establishment time — no silent
        // fail-open path remains.
        var act = () => _server.IssueSession("ada", "conv-1", Address.AgentScheme);

        Should.Throw<ArgumentException>(act).ParamName.ShouldBe("agentId");
    }

    [Fact]
    public void IssueSession_UnsupportedCallerKind_ThrowsArgumentException()
    {
        // The session's caller-kind must be a subject scheme so the
        // resolver can route the Address. Connector / human schemes are
        // not subjects and are rejected up-front rather than silently
        // bypassing the grant gate.
        var act = () => _server.IssueSession(Guid.NewGuid().ToString("N"), "conv-1", "connector");

        Should.Throw<ArgumentException>(act).ParamName.ShouldBe("callerKind");
    }

    [Fact]
    public void DuplicateToolRegistration_ThrowsAtConstruction()
    {
        var dup1 = new FakeSkillRegistry("fake.dup");
        var dup2 = new FakeSkillRegistry("fake.dup");

        var act = () => new McpServer(
            [dup1, dup2],
            Options.Create(new McpServerOptions()),
            _loggerFactory);

        Should.Throw<SpringException>(act).Message.ShouldContain("more than one ISkillRegistry");
    }

    [Fact]
    public void Endpoint_IsDerivedFromOptions()
    {
        // ADR-0052 §3: the container-facing endpoint is derived from
        // configuration, not from a started listener. With the MCP surface
        // now served as a Kestrel route, Endpoint is non-null from
        // construction — it always equals McpServerOptions.ContainerEndpoint.
        var server = new McpServer(
            [new FakeSkillRegistry("fake.endpoint")],
            Options.Create(new McpServerOptions
            {
                ContainerHost = "host.docker.internal",
                Port = 5050,
            }),
            _loggerFactory);

        server.Endpoint.ShouldBe("http://host.docker.internal:5050/mcp/");
    }

    [Fact]
    public async Task ToolsCall_WhenSkillThrows_PublishesToolResultActivityEvent()
    {
        // Regression for the silent-failure path: prior to the fix, a tool that threw
        // was logged only via ILogger, leaving the portal activity feed empty even
        // though the operator saw a transient toast. Now the McpServer publishes a
        // ToolResult ActivityEvent (severity Error) onto IActivityEventBus so the
        // failure persists in the feed alongside the matching ToolCall event.
        var recordingBus = new RecordingActivityEventBus();
        var throwingRegistry = new ThrowingSkillRegistry(
            "fake.throws",
            new InvalidOperationException("InstallationId must be configured to create an authenticated client."));

        var agentGuid = Guid.NewGuid();
        var threadGuid = Guid.NewGuid();

        var server = new McpServer(
            [throwingRegistry],
            Options.Create(new McpServerOptions()),
            _loggerFactory,
            scopeFactory: null,
            activityEventBus: recordingBus);

        var session = server.IssueSession(agentGuid.ToString("N"), threadGuid.ToString("N"));

        var json = await McpTestTransport.PostJsonAsync(server, session.Token, new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "fake.throws", arguments = new { } }
        }, TestContext.Current.CancellationToken);

        json.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeTrue();

        recordingBus.Events.Count.ShouldBe(1);
        var ev = recordingBus.Events[0];
        ev.EventType.ShouldBe(ActivityEventType.ToolResult);
        ev.Severity.ShouldBe(ActivitySeverity.Error);
        ev.Source.Scheme.ShouldBe(Address.AgentScheme);
        ev.Source.Id.ShouldBe(agentGuid);
        ev.Summary.ShouldContain("fake.throws");
        ev.Summary.ShouldContain("InstallationId must be configured");
        ev.CorrelationId.ShouldBe(threadGuid.ToString("N"));
    }

    private sealed class ThrowingSkillRegistry : ISkillRegistry
    {
        private readonly string _toolName;
        private readonly Exception _toThrow;

        public ThrowingSkillRegistry(string toolName, Exception toThrow)
        {
            _toolName = toolName;
            _toThrow = toThrow;
        }

        public string Name => "fake";

        public IReadOnlyList<ToolDefinition> GetToolDefinitions()
        {
            var schema = JsonSerializer.SerializeToElement(new { type = "object" });
            return [new ToolDefinition(_toolName, "Always-throwing fake tool.", schema, string.Empty)];
        }

        public Task<JsonElement> InvokeAsync(string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
            => throw _toThrow;
    }

    private sealed class RecordingActivityEventBus : IActivityEventBus
    {
        public List<ActivityEvent> Events { get; } = new();

        // The failure-path test never subscribes; an empty observable satisfies the
        // IActivityObservable contract without pulling Rx into the test assembly.
        public IObservable<ActivityEvent> ActivityStream { get; } = new EmptyActivityStream();

        public Task PublishAsync(ActivityEvent activityEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(activityEvent);
            return Task.CompletedTask;
        }

        private sealed class EmptyActivityStream : IObservable<ActivityEvent>
        {
            public IDisposable Subscribe(IObserver<ActivityEvent> observer) => new NoOpDisposable();
        }

        private sealed class NoOpDisposable : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }

    private sealed class FakeSkillRegistry : ISkillRegistry
    {
        private readonly string _toolName;

        public FakeSkillRegistry(string toolName = "fake.tool")
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
            return [new ToolDefinition(_toolName, "Fake echo tool.", schema, string.Empty)];
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
