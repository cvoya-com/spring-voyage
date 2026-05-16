// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Sample.ToolsAgent;

using Cvoya.Spring.AgentSdk;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Minimal sample agent demonstrating the SDK tool-registration API
/// (#2336 / Sub C of #2332). Registers two <c>acme.*</c> tools and wires
/// <c>GET /a2a/tools</c> onto the listener via the SDK's
/// <see cref="ToolsEndpointExtensions.MapToolsEndpoint"/> extension.
/// </summary>
/// <remarks>
/// Wrapped in a named class (rather than top-level statements) so the
/// auto-generated <c>Program</c> type doesn't collide with other
/// <c>Program</c> classes in the integration-test load context — for
/// example <c>WebApplicationFactory&lt;Program&gt;</c> resolution in the
/// Host.Api integration tests.
/// </remarks>
public static class Program
{
    /// <summary>Entry point.</summary>
    public static async Task Main(string[] args)
    {
        var port = ResolvePort(args);
        var registry = new ToolRegistry();
        registry.RegisterAcmeTools();

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
        builder.Services.AddSingleton<IToolRegistry>(registry);

        var app = builder.Build();
        app.MapToolsEndpoint(registry);
        Console.WriteLine($"acme tools agent listening on http://localhost:{port}{ToolsEndpointExtensions.ToolsPath}");

        await app.RunAsync();
    }

    private static int ResolvePort(string[] args)
    {
        if (args is { Length: > 0 } && int.TryParse(args[0], out var fromArgs))
        {
            return fromArgs;
        }
        var fromEnv = Environment.GetEnvironmentVariable("AGENT_PORT");
        if (!string.IsNullOrEmpty(fromEnv) && int.TryParse(fromEnv, out var parsed))
        {
            return parsed;
        }
        return 8999;
    }
}
