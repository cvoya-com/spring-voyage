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
    /// Port to bind the MCP server to. <c>0</c> selects a random available port,
    /// which is fine for tests but **wrong for production** because agent
    /// containers reach the worker through a host port mapping that has to be
    /// declared at container-start time — the host can only publish a port it
    /// knows up front. Production deployments should set a stable port (the
    /// OSS deploy script defaults to <c>5050</c> via <c>spring.env</c>) and
    /// match it with the <c>spring-worker</c> publish on the host.
    /// </summary>
    public int Port { get; set; } = 0;

    /// <summary>
    /// Listener bind address. Defaults to <c>+</c> (HttpListener strong
    /// wildcard — bind on all local interfaces) so the worker's MCP socket is
    /// reachable from outside its own container once the surrounding port is
    /// published. Tests that need loopback-only isolation can override to
    /// <c>127.0.0.1</c>.
    /// </summary>
    /// <remarks>
    /// Until #1199 is finally resolved (likely as part of the V2.1 ADR 0029
    /// rollout — see #1200), the agent container reaches the worker via
    /// <c>host.docker.internal:&lt;Port&gt;</c>: <c>spring-worker</c> binds
    /// here on all interfaces, <c>deploy.sh</c> publishes the same port from
    /// the worker container to the host, and the agent's
    /// <c>--add-host=host.docker.internal:host-gateway</c> entry routes there
    /// through the host bridge. Loopback-only would silently re-introduce the
    /// MCP-discovery failure that #1199 closes.
    /// </remarks>
    public string BindAddress { get; set; } = "+";

    /// <summary>
    /// The hostname the container uses to reach the MCP server. Defaults to
    /// <c>host.docker.internal</c>, which Linux callers resolve via
    /// <c>ExtraHosts = "host.docker.internal:host-gateway"</c>.
    /// </summary>
    public string ContainerHost { get; set; } = "host.docker.internal";

    /// <summary>
    /// The container-facing MCP endpoint, derived from <see cref="ContainerHost"/>
    /// and <see cref="Port"/> without a started listener. ADR-0052 §3:
    /// endpoint-only consumers (e.g. <c>PersistentAgentLifecycle</c>,
    /// <c>AgentContextBuilder</c>) that do not co-reside with the started
    /// <c>McpServer</c> resolve the endpoint from configuration rather than
    /// from the live <c>McpServer.Endpoint</c>.
    /// </summary>
    /// <remarks>
    /// When <see cref="Port"/> is <c>0</c> this yields a meaningless <c>:0</c>
    /// endpoint — but <see cref="Port"/> <c>== 0</c> is a test-only setting;
    /// production always sets a stable port (the OSS deploy uses <c>5050</c>).
    /// The worker's own started <c>McpServer</c> keeps using its bound port,
    /// which is the only correct value when <see cref="Port"/> is <c>0</c>.
    /// </remarks>
    public string ContainerEndpoint => $"http://{ContainerHost}:{Port}/mcp/";
}
