// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk.Tests;

using System.Net.Sockets;
using System.Text.Json;

using Cvoya.Spring.AgentSdk;
using Cvoya.Spring.Core.Skills;

using Shouldly;

using Xunit;

/// <summary>
/// HTTP-surface coverage for <see cref="ToolsEndpointServer"/> (#2336 /
/// Sub C of #2332). The platform's introspector hits
/// <c>GET /a2a/tools</c> at deploy time; these tests pin the wire shape
/// the introspector consumes.
/// </summary>
public class ToolsEndpointServerTests
{
    private static JsonElement EmptyObject() => JsonDocument.Parse("{}").RootElement.Clone();

    private static int FreePort()
    {
        var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [Fact]
    public async Task GetTools_EmptyRegistry_Returns200WithEmptyArray()
    {
        var registry = new ToolRegistry();
        var port = FreePort();
        using var server = new ToolsEndpointServer(registry, $"http://localhost:{port}/");
        server.Start();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
            var response = await http.GetAsync(ToolsEndpointServer.ToolsPath, TestContext.Current.CancellationToken);
            response.IsSuccessStatusCode.ShouldBeTrue();
            var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            body.ShouldBe("[]");
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task GetTools_RegisteredTools_ReturnsCanonicalJsonShape()
    {
        var registry = new ToolRegistry();
        registry.Register(
            new ToolDefinition("acme.echo", "echoes input", JsonDocument.Parse("""
                {"type":"object","properties":{"value":{"type":"string"}}}
                """).RootElement.Clone()),
            static (args, _) => Task.FromResult(args));
        registry.Register(
            new ToolDefinition("acme.timestamp", "returns now", EmptyObject()),
            static (args, _) => Task.FromResult(args));

        var port = FreePort();
        using var server = new ToolsEndpointServer(registry, $"http://localhost:{port}/");
        server.Start();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
            var response = await http.GetAsync(ToolsEndpointServer.ToolsPath, TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
            response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");

            var bytes = await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);
            using var doc = JsonDocument.Parse(bytes);
            doc.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
            doc.RootElement.GetArrayLength().ShouldBe(2);

            var first = doc.RootElement[0];
            first.GetProperty("name").GetString().ShouldBe("acme.echo");
            first.GetProperty("description").GetString().ShouldBe("echoes input");
            first.GetProperty("inputSchema").GetProperty("type").GetString().ShouldBe("object");
            // The Namespace computed property is NOT part of the wire shape.
            first.TryGetProperty("namespace", out _).ShouldBeFalse();

            var second = doc.RootElement[1];
            second.GetProperty("name").GetString().ShouldBe("acme.timestamp");
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public async Task GetTools_UnknownPath_Returns404()
    {
        var port = FreePort();
        using var server = new ToolsEndpointServer(new ToolRegistry(), $"http://localhost:{port}/");
        server.Start();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
            var response = await http.GetAsync("/no-such-route", TestContext.Current.CancellationToken);
            response.StatusCode.ShouldBe(System.Net.HttpStatusCode.NotFound);
        }
        finally
        {
            await server.StopAsync();
        }
    }

    [Fact]
    public void SerializeTools_DropsNamespaceComputedField()
    {
        var registry = new ToolRegistry();
        registry.Register(
            new ToolDefinition("acme.echo", "x", EmptyObject()),
            static (args, _) => Task.FromResult(args));

        var bytes = ToolsEndpointServer.SerializeTools(registry);
        using var doc = JsonDocument.Parse(bytes);
        var element = doc.RootElement[0];
        element.TryGetProperty("namespace", out _).ShouldBeFalse();
        element.GetProperty("name").GetString().ShouldBe("acme.echo");
    }
}
