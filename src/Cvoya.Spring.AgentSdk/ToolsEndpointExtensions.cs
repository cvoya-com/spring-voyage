// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.AgentSdk;

using System.Text.Json;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

/// <summary>
/// ASP.NET Core extension methods that wire the platform-facing
/// <c>GET /a2a/tools</c> endpoint onto the agent's existing listener.
/// </summary>
/// <remarks>
/// Sub C (#2336) of the Tools wave. The platform-side introspector
/// (<c>IAgentToolsIntrospector</c>) calls <c>GET /a2a/tools</c> on the
/// agent's HTTP listener at deploy time and on image rotation, then
/// caches the array onto the agent's <c>image_tools</c> column.
/// The path matches the A2A bridge prefix so introspection lives on
/// the same per-agent listener — agents don't need a second port.
/// </remarks>
public static class ToolsEndpointExtensions
{
    /// <summary>
    /// Path served by the endpoint. Matches the A2A bridge prefix used
    /// by CLI-wrapped runtimes so the platform introspector hits the
    /// same path regardless of deployment topology.
    /// </summary>
    public const string ToolsPath = "/a2a/tools";

    internal static readonly JsonSerializerOptions ToolsJsonOptions = BuildToolsJsonOptions();

    /// <summary>
    /// Maps <c>GET /a2a/tools</c> on <paramref name="routes"/>, serving
    /// the registered tool set from <paramref name="registry"/>.
    /// </summary>
    /// <param name="routes">The endpoint route builder.</param>
    /// <param name="registry">The agent's tool registry.</param>
    /// <param name="pattern">
    /// Optional custom path. Defaults to <see cref="ToolsPath"/>;
    /// agents almost never need to override this.
    /// </param>
    /// <returns>The endpoint convention builder for further configuration.</returns>
    public static IEndpointConventionBuilder MapToolsEndpoint(
        this IEndpointRouteBuilder routes,
        IToolRegistry registry,
        string pattern = ToolsPath)
    {
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);

        return routes.MapGet(pattern, () =>
            Results.Json(registry.List(), ToolsJsonOptions));
    }

    /// <summary>
    /// Serialises the registry's contents to the wire JSON shape the
    /// platform persists on <c>image_tools</c>. Exposed for tests and
    /// for agents that need the bytes without invoking ASP.NET routing.
    /// </summary>
    public static byte[] SerializeTools(IToolRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return JsonSerializer.SerializeToUtf8Bytes(
            registry.List(),
            ToolsJsonOptions);
    }

    private static JsonSerializerOptions BuildToolsJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        };
        options.Converters.Add(new ToolDefinitionConverter());
        return options;
    }
}
