// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Mcp;

/// <summary>
/// Configuration for the in-process MCP server.
/// </summary>
public class McpServerOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Mcp";

    /// <summary>
    /// Port to bind the MCP server to. <c>0</c> selects a random available port.
    /// Defaults to <c>0</c> (ephemeral) — the dispatcher reads the resolved port
    /// off <c>IMcpServer.Endpoint</c>.
    /// </summary>
    public int Port { get; set; } = 0;

    /// <summary>
    /// The hostname the container uses to reach the MCP server. Defaults to
    /// <c>host.docker.internal</c>, which Linux callers resolve via
    /// <c>ExtraHosts = "host.docker.internal:host-gateway"</c>.
    /// </summary>
    public string ContainerHost { get; set; } = "host.docker.internal";
}