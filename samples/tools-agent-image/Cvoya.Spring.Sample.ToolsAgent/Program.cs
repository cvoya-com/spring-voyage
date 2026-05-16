// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Sample.ToolsAgent;

using Cvoya.Spring.AgentSdk;

/// <summary>
/// Minimal sample agent demonstrating the SDK tool-registration API
/// (#2336 / Sub C of #2332).
/// </summary>
/// <remarks>
/// <para>
/// Registers two <c>acme.*</c> tools and starts the SDK's tools-endpoint
/// server on <c>http://localhost:&lt;port&gt;/</c>. The platform-side
/// introspector hits <c>GET /a2a/tools</c> at deploy time and caches the
/// array onto the <c>image_tools</c> column.
/// </para>
/// <para>
/// The sample is intentionally minimal — it only hosts the
/// tools-introspection endpoint. A full agent image co-locates this
/// listener with the A2A bridge on the same port; the sample stands the
/// listener up directly so the integration test can deploy it without
/// the full bridge.
/// </para>
/// </remarks>
public static class Program
{
    /// <summary>
    /// Default port the listener binds when <c>AGENT_PORT</c> is unset.
    /// Matches the platform's default A2A port (8999) so a local <c>curl
    /// http://localhost:8999/a2a/tools</c> just works.
    /// </summary>
    public const int DefaultPort = 8999;

    /// <summary>Entry point.</summary>
    public static async Task Main(string[] args)
    {
        var port = ResolvePort(args);
        var registry = BuildRegistry();
        using var server = new ToolsEndpointServer(
            registry,
            $"http://localhost:{port}/");
        server.Start();
        Console.WriteLine($"acme tools agent listening on http://localhost:{port}{ToolsEndpointServer.ToolsPath}");

        var stop = new TaskCompletionSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            stop.TrySetResult();
        };
        await stop.Task;
    }

    /// <summary>
    /// Constructs and populates a fresh registry. Exposed so the
    /// integration test can build the same registry shape without
    /// invoking <see cref="Main"/>.
    /// </summary>
    public static IToolRegistry BuildRegistry()
    {
        var registry = new ToolRegistry();
        registry.RegisterAcmeTools();
        return registry;
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
        return DefaultPort;
    }
}
