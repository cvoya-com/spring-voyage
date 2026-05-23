// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk.Tests;

using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;

using Cvoya.Spring.AgentSdk;
using Cvoya.Spring.Core.Skills;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Shouldly;

using Xunit;

/// <summary>
/// Unit tests for the ASP.NET Core <c>MapToolsEndpoint</c> extension
/// added by #2336. Stands up a real <see cref="WebApplication"/> per test
/// on a free port so the wire-shape assertions actually exercise the
/// HTTP path the platform-side introspector hits.
/// </summary>
public sealed class ToolsEndpointExtensionsTests
{
    [Fact]
    public async Task MapToolsEndpoint_ReturnsRegisteredToolsAsJson()
    {
        var registry = BuildRegistryWith(
            new ToolDefinition("acme.echo", "Echo input.", BuildSchema(), string.Empty),
            new ToolDefinition("acme.timestamp", "Now.", BuildSchema(), string.Empty));

        await using var host = await StartHostAsync(registry);

        using var client = new HttpClient();
        var response = await client.GetAsync(new Uri(host.BaseUri, ToolsEndpointExtensions.ToolsPath), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        var json = JsonDocument.Parse(body);
        json.RootElement.ValueKind.ShouldBe(JsonValueKind.Array);
        json.RootElement.GetArrayLength().ShouldBe(2);
        json.RootElement[0].GetProperty("name").GetString().ShouldBe("acme.echo");
        json.RootElement[1].GetProperty("name").GetString().ShouldBe("acme.timestamp");
    }

    [Fact]
    public async Task MapToolsEndpoint_EmptyRegistry_ReturnsEmptyArray()
    {
        var registry = new ToolRegistry();
        await using var host = await StartHostAsync(registry);

        using var client = new HttpClient();
        var response = await client.GetAsync(new Uri(host.BaseUri, ToolsEndpointExtensions.ToolsPath), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        body.ShouldBe("[]");
    }

    [Fact]
    public async Task MapToolsEndpoint_CustomPath_Honored()
    {
        var registry = BuildRegistryWith(new ToolDefinition("acme.echo", "Echo.", BuildSchema(), string.Empty));
        await using var host = await StartHostAsync(registry, pattern: "/custom/tools");

        using var client = new HttpClient();
        var response = await client.GetAsync(new Uri(host.BaseUri, "/custom/tools"), TestContext.Current.CancellationToken);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var defaultPath = await client.GetAsync(new Uri(host.BaseUri, ToolsEndpointExtensions.ToolsPath), TestContext.Current.CancellationToken);
        defaultPath.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public void SerializeTools_ProducesUtf8JsonBytes()
    {
        var registry = BuildRegistryWith(new ToolDefinition("acme.echo", "Echo.", BuildSchema(), string.Empty));
        var bytes = ToolsEndpointExtensions.SerializeTools(registry);

        var json = JsonDocument.Parse(bytes);
        json.RootElement.GetArrayLength().ShouldBe(1);
        json.RootElement[0].GetProperty("name").GetString().ShouldBe("acme.echo");
    }

    private static ToolRegistry BuildRegistryWith(params ToolDefinition[] definitions)
    {
        var registry = new ToolRegistry();
        foreach (var def in definitions)
        {
            registry.Register(def, (_, _) => Task.FromResult(JsonDocument.Parse("{}").RootElement.Clone()));
        }
        return registry;
    }

    private static JsonElement BuildSchema() =>
        JsonDocument.Parse("""{"type":"object","properties":{}}""").RootElement.Clone();

    private static async Task<HostHandle> StartHostAsync(
        IToolRegistry registry,
        string pattern = ToolsEndpointExtensions.ToolsPath)
    {
        var port = FreePort();
        var prefix = $"http://localhost:{port}";

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(prefix);
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapToolsEndpoint(registry, pattern);
        await app.StartAsync();
        return new HostHandle(app, new Uri(prefix + "/"));
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed record HostHandle(WebApplication App, Uri BaseUri) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await App.StopAsync(cts.Token); } catch { /* best-effort */ }
            await App.DisposeAsync();
        }
    }
}
