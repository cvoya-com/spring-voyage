// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Tests.Mcp;

using System.Text.Json;

using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Dapr.Mcp;
using Cvoya.Spring.Dapr.Skills;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using NSubstitute;

using Shouldly;

using Xunit;

/// <summary>
/// End-to-end coverage for the ADR-0056 §6 tool-discovery surface
/// (#2656) driven through the platform <see cref="McpServer"/>. Wires a
/// minimal in-process registry stack (the discovery registry + a fake
/// messaging-category tool), drives a session through
/// <see cref="McpServer.HandleRequestAsync"/>, and asserts the
/// <c>sv.tools.list_categories</c> → <c>sv.tools.list("messaging")</c>
/// → <c>tools/call("test.send")</c> flow runs end-to-end with the
/// schemas the discovery surface advertised.
/// </summary>
public class McpToolsDiscoveryEndToEndTests
{
    private readonly ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    /// <summary>
    /// Builds an MCP server backed by a tiny DI graph: a fake
    /// messaging-category tool plus the discovery registry itself. The
    /// discovery registry resolves <see cref="ISkillRegistry"/> from
    /// the scope factory on every call, mirroring its production wiring.
    /// </summary>
    private (McpServer Server, FakeMessagingRegistry Messaging) BuildServer()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(_loggerFactory);
        services.AddLogging();
        services.AddSingleton<FakeMessagingRegistry>();
        services.AddSingleton<ISkillRegistry>(sp => sp.GetRequiredService<FakeMessagingRegistry>());
        services.AddSingleton<SvToolsDiscoverySkillRegistry>();
        services.AddSingleton<ISkillRegistry>(sp =>
            sp.GetRequiredService<SvToolsDiscoverySkillRegistry>());
        var sp = services.BuildServiceProvider();

        var registries = sp.GetServices<ISkillRegistry>().ToList();
        var server = new McpServer(
            registries,
            Options.Create(new McpServerOptions()),
            _loggerFactory,
            scopeFactory: sp.GetRequiredService<IServiceScopeFactory>());

        return (server, sp.GetRequiredService<FakeMessagingRegistry>());
    }

    [Fact]
    public async Task ToolsList_IncludesDiscoveryTools()
    {
        var (server, _) = BuildServer();
        var session = server.IssueSession(
            Guid.NewGuid().ToString("N"), "thread-1", Address.AgentScheme);

        var json = await McpTestTransport.PostJsonAsync(
            server, session.Token,
            new { jsonrpc = "2.0", id = 1, method = "tools/list" },
            TestContext.Current.CancellationToken);

        var names = json.GetProperty("result").GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()!)
            .ToList();
        names.ShouldContain(SvToolsDiscoverySkillRegistry.ListCategoriesTool);
        names.ShouldContain(SvToolsDiscoverySkillRegistry.ListTool);
        names.ShouldContain("test.send");
    }

    [Fact]
    public async Task ListCategories_ThroughMcp_ReturnsExpectedCategories()
    {
        var (server, _) = BuildServer();
        var session = server.IssueSession(
            Guid.NewGuid().ToString("N"), "thread-1", Address.AgentScheme);

        var json = await McpTestTransport.PostJsonAsync(
            server, session.Token,
            new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new
                {
                    name = SvToolsDiscoverySkillRegistry.ListCategoriesTool,
                    arguments = new { },
                }
            },
            TestContext.Current.CancellationToken);

        var rawText = json.GetProperty("result").GetProperty("content")[0]
            .GetProperty("text").GetString()!;
        using var doc = JsonDocument.Parse(rawText);
        var categories = doc.RootElement.GetProperty("categories").EnumerateArray()
            .Select(e => e.GetProperty("name").GetString()!)
            .ToList();
        categories.ShouldContain(ToolCategories.Messaging);
        categories.ShouldContain(ToolCategories.Tools);
    }

    [Fact]
    public async Task ListThenCall_RoundTripsTheSchemaToATargetTool()
    {
        // ADR-0056 §6: every listing carries the input schema so a runtime
        // that has heard of a tool already has everything it needs to call
        // it. Drive the path: discovery → list("messaging") → tools/call
        // with a payload that matches the returned schema.
        var (server, messaging) = BuildServer();
        var session = server.IssueSession(
            Guid.NewGuid().ToString("N"), "thread-1", Address.AgentScheme);

        var listResult = await McpTestTransport.PostJsonAsync(
            server, session.Token,
            new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "tools/call",
                @params = new
                {
                    name = SvToolsDiscoverySkillRegistry.ListTool,
                    arguments = new { category = ToolCategories.Messaging },
                }
            },
            TestContext.Current.CancellationToken);

        var listText = listResult.GetProperty("result").GetProperty("content")[0]
            .GetProperty("text").GetString()!;
        using var listDoc = JsonDocument.Parse(listText);
        var listBody = listDoc.RootElement;
        listBody.GetProperty("category").GetString().ShouldBe(ToolCategories.Messaging);
        listBody.GetProperty("usage_guidance").GetString().ShouldNotBeNullOrWhiteSpace();
        var tools = listBody.GetProperty("tools").EnumerateArray().ToList();
        tools.ShouldHaveSingleItem();
        var sendTool = tools[0];
        sendTool.GetProperty("name").GetString().ShouldBe("test.send");
        // The schema MUST carry the property the next call uses.
        sendTool.GetProperty("input_schema").GetProperty("properties")
            .TryGetProperty("payload", out _).ShouldBeTrue();

        // Use the discovered schema to drive the actual call.
        var callResult = await McpTestTransport.PostJsonAsync(
            server, session.Token,
            new
            {
                jsonrpc = "2.0",
                id = 2,
                method = "tools/call",
                @params = new
                {
                    name = "test.send",
                    arguments = new { payload = "hello via discovery" },
                }
            },
            TestContext.Current.CancellationToken);
        callResult.GetProperty("result").GetProperty("isError").GetBoolean().ShouldBeFalse();

        // The fake messaging registry recorded the payload, confirming
        // the schema-driven invocation completed end-to-end.
        messaging.LastPayload.ShouldBe("hello via discovery");
    }

    /// <summary>
    /// Fake <see cref="ISkillRegistry"/> exposing one tool stamped with
    /// the messaging category. Records the last payload it received so
    /// the round-trip test can assert the call completed end-to-end.
    /// </summary>
    private sealed class FakeMessagingRegistry : ISkillRegistry
    {
        private readonly ToolDefinition _tool = new(
            "test.send",
            "Send a thing.",
            JsonDocument.Parse(
                """
                {
                  "type": "object",
                  "additionalProperties": false,
                  "required": ["payload"],
                  "properties": {
                    "payload": { "type": "string" }
                  }
                }
                """).RootElement,
            ToolCategories.Messaging);

        public string LastPayload { get; private set; } = string.Empty;

        public string Name => "test";
        public IReadOnlyList<ToolDefinition> GetToolDefinitions() => new[] { _tool };
        public Task<JsonElement> InvokeAsync(
            string toolName, JsonElement arguments, CancellationToken cancellationToken = default)
        {
            LastPayload = arguments.GetProperty("payload").GetString() ?? string.Empty;
            return Task.FromResult(JsonDocument.Parse("""{ "ok": true }""").RootElement);
        }
    }
}
