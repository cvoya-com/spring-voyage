// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.ModelProviders;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
    IEnumerable<IAgentRuntimeLauncher> launchers,
    IRuntimeCatalog runtimeCatalog,
    PersistentAgentRegistry persistentAgentRegistry,
    ContainerLifecycleManager containerLifecycleManager,
    AgentVolumeManager volumeManager,
    IOptions<DaprSidecarOptions> daprSidecarOptions,
    ILoggerFactory loggerFactory)
{
    private readonly ILogger _logger = loggerFactory.CreateLogger<PersistentAgentLifecycle>();
    private readonly DaprSidecarOptions _daprSidecarOptions = daprSidecarOptions.Value;
    // ADR-0038: launchers are keyed on the catalogue runtime entry's
    // launcher strategy id; the lifecycle service derives that id from
    // the agent's persisted execution.agent slot (= ai.runtime) via the
    // runtime catalogue.
    private readonly Dictionary<string, IAgentRuntimeLauncher> _launchersByKind =
        launchers.ToDictionary(l => l.Kind, StringComparer.OrdinalIgnoreCase);
    private readonly IRuntimeCatalog _runtimeCatalog = runtimeCatalog
        ?? throw new ArgumentNullException(nameof(runtimeCatalog));

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
                $"Agent '{agentId}' has no execution configuration; " +
                "set ai.agent (the runtime registry id) in the agent / unit YAML before deploying.");
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

        var runtime = _runtimeCatalog.GetAgentRuntime(definition.Execution.AgentRuntimeId)
            ?? throw new SpringException(
                $"No agent runtime is registered in platform/runtime-catalog.yaml with id " +
                $"'{definition.Execution.AgentRuntimeId}' (agent '{agentId}'). Add the entry " +
                "to the catalogue or set ai.runtime to a registered runtime id before deploying.");
        if (!_launchersByKind.TryGetValue(runtime.Launcher, out var launcher))
        {
            throw new SpringException(
                $"No IAgentRuntimeLauncher registered for launcher strategy id " +
                $"'{runtime.Launcher}' (agent runtime '{definition.Execution.AgentRuntimeId}', " +
                $"agent '{agentId}').");
        }
        var kind = runtime.Launcher;

        if (mcpServer.Endpoint is null)
        {
            throw new SpringException("MCP server has not been started; endpoint is unavailable.");
        }

        var sessionId = $"persistent-{agentId}";
        var prompt = definition.Instructions ?? string.Empty;
        var session = mcpServer.IssueSession(agentId, sessionId);

        var launchContext = new AgentLaunchContext(
            AgentId: agentId,
            ThreadId: sessionId,
            Prompt: prompt,
            McpEndpoint: mcpServer.Endpoint,
            McpToken: session.Token,
            TenantId: Cvoya.Spring.Core.Tenancy.OssTenantIds.Default,
            Provider: definition.Execution.Provider,
            Model: definition.Execution.Model);

        var prep = await launcher.PrepareAsync(launchContext, cancellationToken);

        _logger.LogInformation(
            "Deploying persistent agent {AgentId} with image {Image}",
            agentId, image);

        // D3c: provision the per-agent workspace volume before starting the
        // container. Volume survives restarts; reclamation happens on
        // UndeployAsync (which delegates to PersistentAgentRegistry.UndeployAsync).
        var volumeName = await volumeManager.EnsureAsync(agentId, cancellationToken);
        var volumeMount = AgentVolumeManager.BuildVolumeMount(volumeName);
        var prepWithVolume = prep with
        {
            ExtraVolumeMounts = MergeVolumeMounts(prep.ExtraVolumeMounts, volumeMount),
        };

        // We pass the (possibly overridden) image into the ContainerConfig so
        // the override path only affects this single deployment — the stored
        // AgentExecutionConfig.Image is untouched. Workspace materialisation
        // lives in the dispatcher service (issue #1042); the launcher only
        // describes the workspace files + mount path here. The shared
        // ContainerConfigBuilder is the single seam that translates the
        // launch spec into a container config across all dispatch paths.
        var baseConfig = ContainerConfigBuilder.Build(image, prepWithVolume);
        var useDaprSidecar = string.Equals(
            kind, LauncherIds.SpringVoyageAgent, StringComparison.OrdinalIgnoreCase);

        string containerId;
        string? sidecarId = null;
        string? lifecycleNetworkName = null;
        if (useDaprSidecar)
        {
            var daprAppId = BuildPersistentDaprAppIdForDeploy(agentId);
            var daprConfig = baseConfig with
            {
                DaprAppId = daprAppId,
                DaprAppPort = prep.A2APort,
                DaprSidecarComponentsPath = _daprSidecarOptions.DelegatedSpringVoyageAgentComponentsPath,
            };
            var detached = await containerLifecycleManager.LaunchWithSidecarDetachedAsync(
                daprConfig, cancellationToken);
            containerId = detached.ContainerId;
            sidecarId = detached.SidecarInfo.SidecarId;
            lifecycleNetworkName = detached.NetworkName;
        }
        else
        {
            containerId = await containerRuntime.StartAsync(baseConfig, cancellationToken);
        }

        var endpoint = new Uri($"http://localhost:{prep.A2APort}/");

        var ready = await persistentAgentRegistry.WaitForA2AReadyAsync(
            containerId, endpoint, A2AExecutionDispatcher.ReadinessTimeout, cancellationToken);

        if (!ready)
        {
            _logger.LogError(
                "Persistent agent {AgentId} did not become ready within {Timeout}. Stopping container.",
                agentId, A2AExecutionDispatcher.ReadinessTimeout);
            if (useDaprSidecar && sidecarId is not null && lifecycleNetworkName is not null)
            {
                await containerLifecycleManager.TeardownAsync(
                    containerId, sidecarId, lifecycleNetworkName, CancellationToken.None);
            }
            else
            {
                await containerRuntime.StopAsync(containerId, CancellationToken.None);
            }

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

        persistentAgentRegistry.Register(
            agentId, endpoint, containerId, effectiveDefinition, sidecarId, lifecycleNetworkName);

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

    private static IReadOnlyList<string> MergeVolumeMounts(
        IReadOnlyList<string>? existing,
        string additionalMount)
    {
        if (existing is null || existing.Count == 0)
        {
            return [additionalMount];
        }

        var merged = new List<string>(existing.Count + 1);
        merged.AddRange(existing);
        merged.Add(additionalMount);
        return merged;
    }

    /// <summary>Matches <see cref="A2AExecutionDispatcher"/>'s stable app-id for persistent dapr agents.</summary>
    private static string BuildPersistentDaprAppIdForDeploy(string agentId)
    {
        var id = agentId.Replace("/", "-", StringComparison.Ordinal).Replace(":", "-", StringComparison.Ordinal);
        if (id.Length > 32)
        {
            id = id[^32..];
        }

        return "p" + id;
    }
}