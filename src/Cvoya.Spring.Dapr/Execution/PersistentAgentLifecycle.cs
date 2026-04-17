// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;

using Microsoft.Extensions.Logging;

/// <summary>
/// Imperative lifecycle controller for persistent agents. Backs the CLI
/// surface added in #396 (<c>spring agent deploy / scale / logs / undeploy</c>)
/// and the HTTP endpoints at <c>/api/v1/agents/{id}/deploy</c> etc.
/// </summary>
/// <remarks>
/// The auto-start path inside <see cref="A2AExecutionDispatcher"/> — which
/// starts a persistent container on first dispatch — is still the zero-config
/// default. This service exists so operators can stand up (or tear down) a
/// persistent agent explicitly, without waiting for inbound traffic, and so
/// the CLI has a surface for inspecting container health and logs.
///
/// The service is intentionally thin: it assembles an
/// <see cref="AgentLaunchContext"/>, forwards it to the matching launcher,
/// starts a container, probes the A2A readiness, and registers the result
/// with <see cref="PersistentAgentRegistry"/>. Undeploy delegates straight to
/// the registry. Horizontal scale (<c>Replicas &gt; 1</c>) is out of scope in
/// the OSS core today — callers get a clear error message.
/// </remarks>
public class PersistentAgentLifecycle(
    IContainerRuntime containerRuntime,
    IAgentDefinitionProvider agentDefinitionProvider,
    IMcpServer mcpServer,
    IEnumerable<IAgentToolLauncher> launchers,
    PersistentAgentRegistry persistentAgentRegistry,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<PersistentAgentLifecycle>();
    private readonly Dictionary<string, IAgentToolLauncher> _launchersByTool =
        launchers.ToDictionary(l => l.Tool, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Stands up a persistent agent. Idempotent: when the agent is already
    /// running and healthy, the current entry is returned without touching
    /// the container. When the agent is unhealthy the old container is
    /// stopped and a fresh one is started.
    /// </summary>
    /// <param name="agentId">The agent identifier.</param>
    /// <param name="imageOverride">
    /// Optional image to run instead of the one baked in the definition. The
    /// override is applied to this deployment only; it is not persisted.
    /// </param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The entry registered in the persistent registry.</returns>
    /// <exception cref="SpringException">
    /// Thrown when the agent has no stored definition, no execution image,
    /// no matching launcher, or fails the readiness probe.
    /// </exception>
    public async Task<PersistentAgentEntry> DeployAsync(
        string agentId,
        string? imageOverride = null,
        CancellationToken cancellationToken = default)
    {
        var definition = await agentDefinitionProvider.GetByIdAsync(agentId, cancellationToken)
            ?? throw new SpringException($"No agent definition found for '{agentId}'.");

        if (definition.Execution is null)
        {
            throw new SpringException(
                $"Agent '{agentId}' has no execution configuration; set execution.tool in the agent YAML before deploying.");
        }

        if (definition.Execution.Hosting != AgentHostingMode.Persistent)
        {
            throw new SpringException(
                $"Agent '{agentId}' is not configured as persistent (hosting='{definition.Execution.Hosting}'). " +
                "Set execution.hosting: persistent before calling deploy.");
        }

        // Idempotent fast-path: healthy entry already registered.
        if (persistentAgentRegistry.TryGet(agentId, out var existing) && existing is not null &&
            existing.HealthStatus == AgentHealthStatus.Healthy)
        {
            return existing;
        }

        // If an unhealthy entry exists, stop it first so we don't leak the
        // old container. UndeployAsync is safe when nothing is tracked.
        await persistentAgentRegistry.UndeployAsync(agentId, cancellationToken);

        var image = string.IsNullOrWhiteSpace(imageOverride)
            ? definition.Execution.Image
            : imageOverride;

        if (image is null)
        {
            throw new SpringException(
                $"Persistent agent '{agentId}' requires a container image. " +
                "Set execution.image in the agent YAML or pass --image on deploy.");
        }

        if (!_launchersByTool.TryGetValue(definition.Execution.Tool, out var launcher))
        {
            throw new SpringException(
                $"No IAgentToolLauncher registered for tool '{definition.Execution.Tool}' (agent '{agentId}').");
        }

        if (mcpServer.Endpoint is null)
        {
            throw new SpringException("MCP server has not been started; endpoint is unavailable.");
        }

        var sessionId = $"persistent-{agentId}";
        var prompt = definition.Instructions ?? string.Empty;
        var session = mcpServer.IssueSession(agentId, sessionId);

        var launchContext = new AgentLaunchContext(
            AgentId: agentId,
            ConversationId: sessionId,
            Prompt: prompt,
            McpEndpoint: mcpServer.Endpoint,
            McpToken: session.Token,
            Provider: definition.Execution.Provider,
            Model: definition.Execution.Model);

        var prep = await launcher.PrepareAsync(launchContext, cancellationToken);

        _logger.LogInformation(
            "Deploying persistent agent {AgentId} with image {Image}",
            agentId, image);

        // We pass the (possibly overridden) image into the ContainerConfig so
        // the override path only affects this single deployment — the stored
        // AgentExecutionConfig.Image is untouched.
        var config = new ContainerConfig(
            Image: image,
            EnvironmentVariables: prep.EnvironmentVariables,
            VolumeMounts: prep.VolumeMounts,
            ExtraHosts: ["host.docker.internal:host-gateway"],
            WorkingDirectory: prep.WorkingDirectory.Contains(':')
                ? null
                : prep.WorkingDirectory);

        var containerId = await containerRuntime.StartAsync(config, cancellationToken);
        var endpoint = new Uri($"http://localhost:{A2AExecutionDispatcher.SidecarPort}/");

        var ready = await persistentAgentRegistry.WaitForA2AReadyAsync(
            endpoint, A2AExecutionDispatcher.ReadinessTimeout, cancellationToken);

        if (!ready)
        {
            _logger.LogError(
                "Persistent agent {AgentId} did not become ready within {Timeout}. Stopping container.",
                agentId, A2AExecutionDispatcher.ReadinessTimeout);
            await containerRuntime.StopAsync(containerId, CancellationToken.None);
            throw new SpringException(
                $"Persistent agent '{agentId}' did not become ready within {A2AExecutionDispatcher.ReadinessTimeout}.");
        }

        // Clone the definition so the stored image override doesn't leak back
        // into the in-memory AgentDefinition owned by callers.
        var effectiveDefinition = image == definition.Execution.Image
            ? definition
            : definition with
            {
                Execution = definition.Execution with { Image = image }
            };

        persistentAgentRegistry.Register(agentId, endpoint, containerId, effectiveDefinition);

        // TryGet immediately after Register so we return the canonical entry
        // rather than a locally-constructed copy.
        persistentAgentRegistry.TryGet(agentId, out var registered);
        return registered!;
    }

    /// <summary>
    /// Tears down a persistent agent deployment. Idempotent — returns
    /// <c>false</c> when nothing was tracked. Delegates to
    /// <see cref="PersistentAgentRegistry.UndeployAsync"/>.
    /// </summary>
    public Task<bool> UndeployAsync(string agentId, CancellationToken cancellationToken = default)
        => persistentAgentRegistry.UndeployAsync(agentId, cancellationToken);

    /// <summary>
    /// Reads the tail of a persistent agent's container logs.
    /// </summary>
    /// <exception cref="SpringException">
    /// Thrown when the agent is not currently deployed.
    /// </exception>
    public async Task<string> GetLogsAsync(
        string agentId,
        int tail = 200,
        CancellationToken cancellationToken = default)
    {
        if (!persistentAgentRegistry.TryGet(agentId, out var entry) || entry?.ContainerId is null)
        {
            throw new SpringException(
                $"Persistent agent '{agentId}' is not deployed; nothing to read logs from.");
        }

        return await containerRuntime.GetLogsAsync(entry.ContainerId, tail, cancellationToken);
    }

    /// <summary>
    /// Applies a replica-count change. The OSS core only supports
    /// <c>replicas == 1</c> today; horizontal scaling is a tracked follow-up.
    /// Callers passing anything else get a clear exception that the endpoint
    /// surfaces as a 400.
    /// </summary>
    public async Task<PersistentAgentEntry> ScaleAsync(
        string agentId,
        int replicas,
        CancellationToken cancellationToken = default)
    {
        if (replicas < 0)
        {
            throw new SpringException("Replica count must be non-negative.");
        }

        if (replicas == 0)
        {
            // Scale to zero is equivalent to undeploy; clients may choose
            // either verb.
            await persistentAgentRegistry.UndeployAsync(agentId, cancellationToken);
            return new PersistentAgentEntry(
                agentId,
                Endpoint: new Uri("http://localhost/"),
                ContainerId: null,
                StartedAt: DateTimeOffset.UtcNow,
                HealthStatus: AgentHealthStatus.Unhealthy,
                ConsecutiveFailures: 0,
                Definition: null);
        }

        if (replicas > 1)
        {
            throw new SpringException(
                "Horizontal scaling (replicas > 1) is not supported by the OSS core yet. " +
                "Keep replicas == 1; a follow-up will add container pooling.");
        }

        // replicas == 1 is equivalent to "ensure deployed". Reuse the deploy
        // path with no image override.
        return await DeployAsync(agentId, imageOverride: null, cancellationToken);
    }
}