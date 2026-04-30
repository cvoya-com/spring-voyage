// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Text.Json;

using A2A.V0_3;

using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Tenancy;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

using SvMessage = Cvoya.Spring.Core.Messaging.Message;

/// <summary>
/// <see cref="IExecutionDispatcher"/> implementation that communicates with
/// agents via the A2A (Agent-to-Agent) protocol. PR 5 of the #1087 series
/// collapsed the legacy "ephemeral agents go through
/// <c>RunAsync + harvest stdout</c>" branch onto the same A2A path that
/// persistent agents have always used:
/// <list type="number">
///   <item>Resolve image and <see cref="AgentLaunchSpec"/> via the launcher.</item>
///   <item>Build the container config via <see cref="ContainerConfigBuilder"/>.</item>
///   <item>Start the container in detached mode (<see cref="IContainerRuntime.StartAsync"/>).</item>
///   <item>Wait for the in-container A2A endpoint to become ready (<c>GET /.well-known/agent.json</c>).</item>
///   <item>Send the platform message via <see cref="SendA2AMessageAsync"/>.</item>
///   <item>Map the A2A response back to a Spring Voyage <see cref="SvMessage"/>.</item>
///   <item><b>Ephemeral</b>: tear down the container; <b>persistent</b>: leave it running.</item>
/// </list>
/// This is the change that fixes the symptom in #1087 — ephemeral agents no
/// longer get stuck on <c>sleep infinity</c> because the dispatcher no longer
/// waits for the container's stdout to terminate.
/// <para>
/// D2 / Stage 2 of ADR-0029: all A2A message-send calls now flow through
/// <see cref="IA2ATransportFactory"/> so auth, routing, and network-position
/// decisions are encapsulated in the transport and not threaded inline here.
/// This subsumes the "extract IAgentTransport" cleanup noted in #1277.
/// </para>
/// </summary>
public class A2AExecutionDispatcher(
    IContainerRuntime containerRuntime,
    IPromptAssembler promptAssembler,
    IAgentDefinitionProvider agentDefinitionProvider,
    IMcpServer mcpServer,
    IEnumerable<IAgentToolLauncher> launchers,
    IAgentContextBuilder agentContextBuilder,
    ITenantContext tenantContext,
    PersistentAgentRegistry persistentAgentRegistry,
    EphemeralAgentRegistry ephemeralAgentRegistry,
    ContainerLifecycleManager containerLifecycleManager,
    AgentVolumeManager volumeManager,
    IOptions<DaprSidecarOptions> daprSidecarOptions,
    IA2ATransportFactory transportFactory,
    ILoggerFactory loggerFactory) : IExecutionDispatcher
{
    private static readonly ISerializer _yamlSerializer = new SerializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    private readonly ILogger _logger = loggerFactory.CreateLogger<A2AExecutionDispatcher>();
    private readonly DaprSidecarOptions _daprSidecarOptions = daprSidecarOptions.Value;
    private readonly IA2ATransportFactory _transportFactory = transportFactory
        ?? throw new ArgumentNullException(nameof(transportFactory));
    private readonly IAgentContextBuilder _agentContextBuilder = agentContextBuilder
        ?? throw new ArgumentNullException(nameof(agentContextBuilder));
    private readonly ITenantContext _tenantContext = tenantContext
        ?? throw new ArgumentNullException(nameof(tenantContext));
    private readonly Dictionary<string, IAgentToolLauncher> _launchersByTool =
        launchers.ToDictionary(l => l.Tool, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Default port the in-container A2A endpoint listens on. Mirrors the
    /// agent-base bridge's default and the Dapr Agent's <c>AGENT_PORT</c>.
    /// </summary>
    internal const int SidecarPort = 8999;

    /// <summary>
    /// Maximum time to wait for the in-container A2A endpoint to become ready.
    /// The bridge starts in well under a second; 60s is generous and tolerates
    /// slow-pull cold starts.
    /// </summary>
    internal static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Interval between readiness probe attempts.
    /// </summary>
    internal static readonly TimeSpan ReadinessProbeInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Effective readiness timeout used by the probe loop. Defaults to
    /// <see cref="ReadinessTimeout"/>. Tests may override this field
    /// after construction to exercise the timeout-expiry branch without
    /// real wall-clock sleep.
    /// </summary>
    internal TimeSpan EffectiveReadinessTimeout = ReadinessTimeout;

    /// <inheritdoc />
    public async Task<SvMessage?> DispatchAsync(
        SvMessage message,
        PromptAssemblyContext? context,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation(
            "Dispatching A2A execution for message {MessageId} to {Destination}",
            message.Id, message.To);

        var agentId = message.To.Path;
        var definition = await agentDefinitionProvider.GetByIdAsync(agentId, cancellationToken)
            ?? throw new SpringException($"No agent definition found for '{agentId}'.");

        if (definition.Execution is null)
        {
            throw new SpringException(
                $"Agent '{agentId}' has no execution configuration; set execution.tool in the agent YAML.");
        }

        return definition.Execution.Hosting switch
        {
            AgentHostingMode.Persistent => await DispatchPersistentAsync(message, definition, context, cancellationToken),
            AgentHostingMode.Ephemeral => await DispatchEphemeralAsync(message, definition, context, cancellationToken),
            // Pooled is reserved on the enum (PR 1 of #1087) so agent YAML
            // written against #362 doesn't break the parser before #362
            // lands. Reject explicitly here so the value can't silently fall
            // through to ephemeral dispatch.
            AgentHostingMode.Pooled => throw new NotSupportedException(
                $"Pooled agent hosting is reserved for #362 and not yet implemented (agent '{agentId}'). " +
                "Set execution.hosting to 'ephemeral' or 'persistent'."),
            _ => throw new NotSupportedException(
                $"Unknown AgentHostingMode '{definition.Execution.Hosting}' for agent '{agentId}'."),
        };
    }

    private async Task<SvMessage?> DispatchEphemeralAsync(
        SvMessage message,
        AgentDefinition definition,
        PromptAssemblyContext? context,
        CancellationToken cancellationToken)
    {
        var agentId = definition.AgentId;

        if (definition.Execution!.Image is null)
        {
            // #601 B-wide: image resolution chain is agent → unit → fail. The
            // provider merges unit defaults before we see the definition here,
            // so a null image at this point means neither surface declared one.
            throw new SpringException(
                $"Ephemeral agent '{agentId}' requires a container image. " +
                "Set execution.image on the agent (spring agent execution set --image) " +
                "or on the parent unit as a default (spring unit execution set --image), " +
                "or switch the agent to hosting: persistent.");
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

        var threadId = message.ThreadId
            ?? throw new SpringException("A2A dispatch requires a thread id on the message.");

        var prompt = await promptAssembler.AssembleAsync(message, context, cancellationToken);
        var session = mcpServer.IssueSession(agentId, threadId);

        // #1321: serialise AgentDefinition → YAML for the /spring/context/
        // agent-definition.yaml file (D1 spec § 2.2.2). Tenant config is
        // delivered as a minimal JSON with the current tenant id — the OSS
        // platform has no separate tenant-config blob.
        var agentDefinitionYaml = SerialiseAgentDefinitionYaml(definition);
        var tenantId = _tenantContext.CurrentTenantId;
        var tenantConfigJson = SerialiseTenantConfigJson(tenantId);

        var launchContext = new AgentLaunchContext(
            AgentId: agentId,
            ThreadId: threadId,
            Prompt: prompt,
            McpEndpoint: mcpServer.Endpoint,
            McpToken: session.Token,
            TenantId: tenantId,
            AgentDefinitionYaml: agentDefinitionYaml,
            TenantConfigJson: tenantConfigJson,
            Provider: definition.Execution.Provider,
            Model: definition.Execution.Model,
            // D3a: populate D1-spec metadata so the context builder can mint the
            // full bootstrap bundle (env vars + /spring/context/ files) per § 2.
            ConcurrentThreads: definition.Execution.ConcurrentThreads);

        // D3a: assemble the IAgentContext bootstrap bundle (env vars + mounted
        // context files) defined by the D1 spec § 2. The bundle is merged into
        // the launcher's spec so every container receives the canonical env var
        // set regardless of tool.
        var bootstrapContext = await _agentContextBuilder.BuildAsync(launchContext, cancellationToken);

        var spec = await launcher.PrepareAsync(launchContext, cancellationToken);

        // D3a: merge bootstrap env vars on top of launcher-produced env vars;
        // merge context files from the bootstrap bundle into the spec. The
        // builder's env vars win on collision (they are the D1-canonical names).
        var specWithContext = MergeBootstrapContext(spec, bootstrapContext);

        // D3c: provision the per-agent workspace volume before starting the
        // container. The volume survives container restarts and mid-flight
        // crashes — only ephemeral completion (ReleaseAsync) triggers
        // reclamation, per ADR-0029 § "Durable state: a per-agent persistent
        // volume".
        var volumeName = await volumeManager.EnsureAsync(agentId, cancellationToken);
        var volumeMount = AgentVolumeManager.BuildVolumeMount(volumeName);
        var specWithVolume = specWithContext with
        {
            ExtraVolumeMounts = MergeVolumeMounts(specWithContext.ExtraVolumeMounts, volumeMount),
        };

        var baseConfig = ContainerConfigBuilder.Build(definition.Execution.Image, specWithVolume);
        var useDaprSidecar = string.Equals(
            definition.Execution.Tool, DaprAgentLauncher.ToolId, StringComparison.OrdinalIgnoreCase);

        string? containerId = null;
        string? sidecarId = null;
        string? lifecycleNetworkName = null;
        EphemeralAgentLease? lease = null;
        try
        {
            if (useDaprSidecar)
            {
                // dapr-agent + dapr-agents 1.x: the Python process needs a
                // daprd with the delegated component profile, placement, and
                // scheduler so the DurableAgent workflow loop can start (see
                // ADR 0028 V2 interim dual-attach deployment).
                var daprAppId = BuildEphemeralDaprAppId();
                var daprConfig = baseConfig with
                {
                    DaprAppId = daprAppId,
                    DaprAppPort = spec.A2APort,
                    DaprSidecarComponentsPath = _daprSidecarOptions.DelegatedDaprAgentComponentsPath,
                };

                var detached = await containerLifecycleManager.LaunchWithSidecarDetachedAsync(
                    daprConfig, cancellationToken);
                containerId = detached.ContainerId;
                sidecarId = detached.SidecarInfo.SidecarId;
                lifecycleNetworkName = detached.NetworkName;
                lease = ephemeralAgentRegistry.Register(
                    agentId, threadId, containerId, sidecarId, lifecycleNetworkName);
            }
            else
            {
                // Detached start: the container runs until we stop it, regardless
                // of what the agent process inside does. This is the seam that
                // fixes #1087 — the dispatcher no longer waits for the agent's
                // stdout to terminate, it talks A2A to the in-container bridge
                // and tears the container down explicitly when the turn drains.
                containerId = await containerRuntime.StartAsync(baseConfig, cancellationToken);
                lease = ephemeralAgentRegistry.Register(agentId, threadId, containerId);
            }

            // The endpoint URI's host is "localhost" because BOTH the
            // readiness probe AND the message-send call now run INSIDE the
            // agent container's own network namespace (via the dispatcher's
            // exec-wget primitives — see WaitForA2AReadyAsync and
            // DispatcherProxyHttpMessageHandler). This is what closes #1160
            // end-to-end: the worker and the agent container can sit on
            // different bridge networks without breaking dispatch.
            var endpoint = new Uri($"http://localhost:{spec.A2APort}/");

            var ready = await WaitForA2AReadyAsync(
                containerId, endpoint, EffectiveReadinessTimeout, cancellationToken);

            if (!ready)
            {
                _logger.LogWarning(
                    "Ephemeral agent {AgentId} (container {ContainerId}) did not become ready within {Timeout}",
                    agentId, containerId, EffectiveReadinessTimeout);
                throw new SpringException(
                    $"Ephemeral agent '{agentId}' did not become A2A-ready within {EffectiveReadinessTimeout}.");
            }

            return await SendA2AMessageAsync(endpoint, agentId, containerId, message, prompt, cancellationToken);
        }
        finally
        {
            mcpServer.RevokeSession(session.Token);
            if (lease.HasValue)
            {
                // Detached from the caller's cancellation token — even if the
                // turn was cancelled we still want to tear the container down,
                // and the registry's release path is idempotent.
                await ephemeralAgentRegistry.ReleaseAsync(lease.Value, CancellationToken.None);
            }
            else if (containerId is not null)
            {
                // Started but never registered (extremely narrow race window
                // — Register is synchronous after StartAsync). Best-effort
                // stop so we don't leak the container.
                try
                {
                    if (useDaprSidecar && sidecarId is not null && lifecycleNetworkName is not null)
                    {
                        await containerLifecycleManager.TeardownAsync(
                            containerId, sidecarId, lifecycleNetworkName, CancellationToken.None);
                    }
                    else
                    {
                        await containerRuntime.StopAsync(containerId, CancellationToken.None);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to stop unregistered ephemeral container {ContainerId}", containerId);
                }
            }
        }
    }

    private static string BuildEphemeralDaprAppId() => $"e{Guid.NewGuid():N}";

    /// <summary>
    /// Merges the <see cref="AgentBootstrapContext"/> assembled by
    /// <see cref="IAgentContextBuilder"/> into a launcher-produced
    /// <see cref="AgentLaunchSpec"/>. Bootstrap env vars win on key collision
    /// (they carry the D1-canonical names); context files are placed in the
    /// spec's <see cref="AgentLaunchSpec.ContextFiles"/> map.
    /// </summary>
    private static AgentLaunchSpec MergeBootstrapContext(
        AgentLaunchSpec spec,
        AgentBootstrapContext bootstrap)
    {
        // Merge env vars: start with launcher values, overlay bootstrap values
        // so D1-canonical names always reflect what the builder computed.
        var mergedEnv = new Dictionary<string, string>(spec.EnvironmentVariables, StringComparer.Ordinal);
        foreach (var kvp in bootstrap.EnvironmentVariables)
        {
            mergedEnv[kvp.Key] = kvp.Value;
        }

        // Merge context files: start with any files the launcher may have put
        // in ContextFiles (unusual but allowed), then add bootstrap files. On
        // collision bootstrap wins for the same reason as env vars.
        Dictionary<string, string> mergedContext;
        if (spec.ContextFiles is { Count: > 0 })
        {
            mergedContext = new Dictionary<string, string>(spec.ContextFiles, StringComparer.Ordinal);
        }
        else
        {
            mergedContext = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        foreach (var kvp in bootstrap.ContextFiles)
        {
            mergedContext[kvp.Key] = kvp.Value;
        }

        return spec with
        {
            EnvironmentVariables = mergedEnv,
            ContextFiles = mergedContext.Count > 0 ? mergedContext : null,
        };
    }

    /// <summary>
    /// Appends <paramref name="additionalMount"/> to an existing mount list,
    /// returning a new list. Used to inject the per-agent workspace volume
    /// mount into a launcher's <see cref="AgentLaunchSpec"/> without mutating
    /// the launcher's immutable record.
    /// </summary>
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

    /// <summary>
    /// Produces a stable, short Dapr <c>app-id</c> for a persistent
    /// <c>dapr-agent</c> so workflow / actor state can survive process restarts.
    /// </summary>
    private static string BuildPersistentDaprAppId(string agentId)
    {
        var id = agentId.Replace("/", "-", StringComparison.Ordinal).Replace(":", "-", StringComparison.Ordinal);
        if (id.Length > 32)
        {
            id = id[^32..];
        }

        return "p" + id;
    }

    private async Task<SvMessage?> DispatchPersistentAsync(
        SvMessage message,
        AgentDefinition definition,
        PromptAssemblyContext? context,
        CancellationToken cancellationToken)
    {
        var agentId = definition.AgentId;
        Uri endpoint;
        string? containerId;

        // Check if the agent service is already running and healthy.
        if (persistentAgentRegistry.TryGet(agentId, out var entry) && entry is not null
            && entry.HealthStatus == AgentHealthStatus.Healthy)
        {
            endpoint = entry.Endpoint;
            containerId = entry.ContainerId;
        }
        else
        {
            // Not running (or unhealthy) — auto-start the agent container.
            (endpoint, containerId) = await StartPersistentAgentAsync(definition, cancellationToken);
        }

        if (string.IsNullOrEmpty(containerId))
        {
            // Legacy externally-registered persistent agents have no container
            // id. Without one the transport factory cannot select the
            // dispatcher-proxy path and falls back to the direct-HTTP path,
            // which requires the caller to have L3 reachability to the agent
            // endpoint. The OSS deployment never registers an agent without a
            // container id; the rare integration test that does should ensure
            // the agent endpoint is reachable from the test process directly.
            // Log a warning so the gap is visible in production deployments.
            _logger.LogWarning(
                "Persistent agent {AgentId} is registered without a container id; " +
                "falling back to direct-HTTP transport (requires L3 reachability to {Endpoint}). " +
                "Re-deploy the agent through the standard persistent path so the registry captures " +
                "the container id (#1160).",
                agentId, endpoint);
        }

        var prompt = await promptAssembler.AssembleAsync(message, context, cancellationToken);

        try
        {
            return await SendA2AMessageAsync(endpoint, agentId, containerId, message, prompt, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Container failed mid-dispatch — mark unhealthy for next dispatch.
            _logger.LogWarning(ex,
                "A2A call to persistent agent {AgentId} failed; marking unhealthy for restart",
                agentId);
            persistentAgentRegistry.MarkUnhealthy(agentId);
            throw;
        }
    }

    /// <summary>
    /// Starts a persistent agent container and registers it in the registry.
    /// Returns both the endpoint and the container id so the caller can
    /// route the A2A message-send call through the dispatcher-proxied
    /// transport (#1160).
    /// </summary>
    private async Task<(Uri Endpoint, string ContainerId)> StartPersistentAgentAsync(
        AgentDefinition definition,
        CancellationToken cancellationToken)
    {
        var agentId = definition.AgentId;

        if (definition.Execution?.Image is null)
        {
            // #601 B-wide: same merge-aware error as the ephemeral path.
            throw new SpringException(
                $"Persistent agent '{agentId}' requires a container image. " +
                "Set execution.image on the agent (spring agent execution set --image) " +
                "or on the parent unit as a default (spring unit execution set --image).");
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

        // Use a stable thread ID for persistent agent MCP sessions.
        var sessionId = $"persistent-{agentId}";
        var prompt = definition.Instructions ?? string.Empty;
        var session = mcpServer.IssueSession(agentId, sessionId);

        // #1321: populate agent definition YAML + tenant config JSON for the
        // /spring/context/ mount (D1 spec § 2.2.2).
        var agentDefinitionYaml = SerialiseAgentDefinitionYaml(definition);
        var tenantId = _tenantContext.CurrentTenantId;
        var tenantConfigJson = SerialiseTenantConfigJson(tenantId);

        var launchContext = new AgentLaunchContext(
            AgentId: agentId,
            ThreadId: sessionId,
            Prompt: prompt,
            McpEndpoint: mcpServer.Endpoint,
            McpToken: session.Token,
            TenantId: tenantId,
            AgentDefinitionYaml: agentDefinitionYaml,
            TenantConfigJson: tenantConfigJson,
            Provider: definition.Execution.Provider,
            Model: definition.Execution.Model,
            // D3a: populate D1-spec metadata for context builder.
            ConcurrentThreads: definition.Execution.ConcurrentThreads);

        // D3a: assemble the IAgentContext bootstrap bundle (env vars + /spring/context/ files).
        var bootstrapContext = await _agentContextBuilder.BuildAsync(launchContext, cancellationToken);

        var spec = await launcher.PrepareAsync(launchContext, cancellationToken);

        // D3a: merge bootstrap bundle into launcher spec.
        var specWithContext = MergeBootstrapContext(spec, bootstrapContext);

        _logger.LogInformation(
            "Starting persistent agent {AgentId} with image {Image}",
            agentId, definition.Execution.Image);

        // D3c: provision the per-agent workspace volume before starting the
        // container. For persistent agents the volume survives restarts —
        // reclamation only happens on explicit undeploy (UndeployAsync).
        var volumeName = await volumeManager.EnsureAsync(agentId, cancellationToken);
        var volumeMount = AgentVolumeManager.BuildVolumeMount(volumeName);
        var specWithVolume = specWithContext with
        {
            ExtraVolumeMounts = MergeVolumeMounts(specWithContext.ExtraVolumeMounts, volumeMount),
        };

        var baseConfig = ContainerConfigBuilder.Build(definition.Execution.Image, specWithVolume);
        var useDaprSidecar = string.Equals(
            definition.Execution.Tool, DaprAgentLauncher.ToolId, StringComparison.OrdinalIgnoreCase);

        string containerId;
        string? sidecarId = null;
        string? lifecycleNetworkName = null;
        if (useDaprSidecar)
        {
            var daprAppId = BuildPersistentDaprAppId(agentId);
            var daprConfig = baseConfig with
            {
                DaprAppId = daprAppId,
                DaprAppPort = spec.A2APort,
                DaprSidecarComponentsPath = _daprSidecarOptions.DelegatedDaprAgentComponentsPath,
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

        var endpoint = new Uri($"http://localhost:{spec.A2APort}/");

        var ready = await WaitForA2AReadyAsync(containerId, endpoint, EffectiveReadinessTimeout, cancellationToken);

        if (!ready)
        {
            _logger.LogError(
                "Persistent agent {AgentId} did not become ready within {Timeout}. Stopping container.",
                agentId, EffectiveReadinessTimeout);
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
                $"Persistent agent '{agentId}' did not become ready within {EffectiveReadinessTimeout}.");
        }

        // Register in the persistent registry.
        persistentAgentRegistry.Register(agentId, endpoint, containerId, definition, sidecarId, lifecycleNetworkName);

        _logger.LogInformation(
            "Persistent agent {AgentId} started and registered at {Endpoint} (container {ContainerId})",
            agentId, endpoint, containerId);

        return (endpoint, containerId);
    }

    /// <summary>
    /// Sends a message to a running A2A agent and collects the response.
    /// Used by both the ephemeral and persistent dispatch paths after the
    /// in-container A2A endpoint has been observed ready.
    /// </summary>
    /// <remarks>
    /// <para>
    /// D2 / Stage 2 of ADR-0029: the HTTP transport is now selected by
    /// <see cref="IA2ATransportFactory"/> rather than being hardwired to
    /// <see cref="DispatcherProxyHttpMessageHandler"/>. The factory returns
    /// the correct transport for the caller's network position (proxy via
    /// the dispatcher, or direct HTTP when the caller can reach the agent
    /// container). This is the named seam that subsumes #1277.
    /// </para>
    /// <para>
    /// The readiness probe still goes through
    /// <see cref="IContainerRuntime.ProbeContainerHttpAsync"/> (unchanged),
    /// which is the mechanism that works regardless of network topology per
    /// issue #1160.
    /// </para>
    /// </remarks>
    internal async Task<SvMessage?> SendA2AMessageAsync(
        Uri endpoint,
        string agentId,
        string? containerId,
        SvMessage originalMessage,
        string prompt,
        CancellationToken cancellationToken)
    {
        using var transport = _transportFactory.CreateTransport(containerId);
        using var httpClient = transport.CreateHttpClient(endpoint);
        var a2aClient = new A2AClient(endpoint, httpClient);

        var userMessage = prompt;
        if (originalMessage.Payload.ValueKind == JsonValueKind.Object &&
            originalMessage.Payload.TryGetProperty("Task", out var taskProp) &&
            taskProp.ValueKind == JsonValueKind.String)
        {
            userMessage = taskProp.GetString() ?? prompt;
        }

        // A2A v0.3 wire shape: MessageSendParams { message, configuration } —
        // the JSON-RPC method name is `message/send` (set by the SDK), which
        // is what the Python a2a-sdk server in the dapr-agent image expects.
        // Parts is List<Part> with derived TextPart/FilePart/DataPart; the
        // discriminator (`kind`) is set by the constructor on each subtype.
        var request = new MessageSendParams
        {
            Message = new AgentMessage
            {
                Role = MessageRole.User,
                Parts = [new TextPart { Text = userMessage }],
                MessageId = originalMessage.Id.ToString(),
                ContextId = originalMessage.ThreadId,
            },
            Configuration = new MessageSendConfiguration
            {
                AcceptedOutputModes = ["text/plain"],
            },
        };

        var response = await a2aClient.SendMessageAsync(request, cancellationToken);

        // The Python `a2a-sdk` `message/send` handler returns the *initial*
        // Task as soon as the executor has accepted the message — typically
        // `state = Submitted` — and continues running the agent loop in
        // the background. If we returned that snapshot to the caller every
        // dispatch would surface as "exit code 1" (the dispatcher reads
        // anything other than `Completed` as failure) and the container
        // would be torn down mid-loop.
        //
        // A2A v0.3 expects the client to poll `tasks/get` on a
        // non-terminal Task. Do that here, holding the ephemeral
        // container's lease open until the workflow reaches a terminal
        // state or the bounded deadline below trips.
        if (response is AgentTask initialTask
            && !IsTerminalTaskState(initialTask.Status.State))
        {
            response = await PollTaskUntilTerminalAsync(
                a2aClient, initialTask, agentId, containerId, cancellationToken);
        }

        return MapA2AResponseToMessage(originalMessage, response);
    }

    /// <summary>
    /// Maximum wall-clock time to wait for an A2A task to reach a terminal
    /// state via <c>tasks/get</c> polling. Sized to comfortably cover an
    /// LLM agentic loop (Ollama on a slow host can stretch into minutes
    /// per turn). The cancellation token from the dispatch call still
    /// applies, so an outer cancel (actor-turn deadline, agent
    /// cancellation) will short-circuit the wait.
    /// </summary>
    internal static readonly TimeSpan TaskTerminalTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Interval between successive <c>tasks/get</c> polls while waiting on
    /// a non-terminal task. Tight enough that completed turns surface
    /// without noticeable extra latency, loose enough to keep dispatcher
    /// proxy load bounded.
    /// </summary>
    internal static readonly TimeSpan TaskPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Effective task-terminal timeout used by the polling loop. Defaults to
    /// <see cref="TaskTerminalTimeout"/>. Tests may override this field
    /// after construction to exercise the timeout-expiry branch without
    /// real wall-clock sleep.
    /// </summary>
    internal TimeSpan EffectiveTaskTerminalTimeout = TaskTerminalTimeout;

    private async Task<A2AResponse> PollTaskUntilTerminalAsync(
        A2AClient a2aClient,
        AgentTask initialTask,
        string agentId,
        string? containerId,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Polling A2A task {TaskId} for terminal state (initial={InitialState}) — agent {AgentId} container {ContainerId}",
            initialTask.Id, initialTask.Status.State, agentId, containerId);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(EffectiveTaskTerminalTimeout);

        var current = initialTask;
        var attempts = 0;
        while (!IsTerminalTaskState(current.Status.State))
        {
            try
            {
                await Task.Delay(TaskPollInterval, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "A2A task {TaskId} did not reach a terminal state within {Timeout} (last state={State}, attempts={Attempts}) — agent {AgentId} container {ContainerId}",
                    current.Id, EffectiveTaskTerminalTimeout, current.Status.State, attempts, agentId, containerId);
                break;
            }

            attempts++;
            try
            {
                current = await a2aClient.GetTaskAsync(current.Id, timeoutCts.Token);
            }
            catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    "A2A task {TaskId} polling timed out within {Timeout} mid-poll (last state={State}, attempts={Attempts}) — agent {AgentId} container {ContainerId}",
                    current.Id, EffectiveTaskTerminalTimeout, current.Status.State, attempts, agentId, containerId);
                break;
            }
        }

        _logger.LogInformation(
            "A2A task {TaskId} terminal-state poll resolved with state={State} after {Attempts} attempts — agent {AgentId} container {ContainerId}",
            current.Id, current.Status.State, attempts, agentId, containerId);

        return current;
    }

    /// <summary>
    /// Whether the A2A v0.3 task state is a terminal one — i.e. the agent
    /// is finished doing work for this turn. Anything that still has work
    /// queued (Submitted, Working) means we should keep polling. Note
    /// <see cref="TaskState.InputRequired"/> is treated as terminal: the
    /// agent is blocked waiting on the caller, and the platform surfaces
    /// that state up to the calling actor rather than spinning on
    /// <c>tasks/get</c> indefinitely.
    /// </summary>
    private static bool IsTerminalTaskState(TaskState state) => state switch
    {
        TaskState.Submitted => false,
        TaskState.Working => false,
        _ => true,
    };

    /// <summary>
    /// Polls the agent container's A2A Agent Card endpoint from the host
    /// until it answers 200 or the timeout expires. Used by both dispatch
    /// paths so they cannot drift on what "ready" means.
    /// </summary>
    /// <remarks>
    /// The probe goes through
    /// <see cref="IContainerRuntime.ProbeHttpFromHostAsync"/> rather than a
    /// direct <see cref="HttpClient"/> call or <c>podman exec</c> so it works
    /// regardless of the worker's network topology and does not depend on any
    /// binary (<c>wget</c>, <c>curl</c>) being present in the workload image.
    /// The host-side probe resolves the container's bridge IP via
    /// <c>podman inspect</c> and issues a plain HTTP GET from the dispatcher
    /// process, avoiding the per-probe <c>podman exec</c> round-trip and the
    /// BYOI fragility documented in issue #1175. When the worker is not
    /// dual-homed on the agent's network, the probe is forwarded through
    /// <see cref="DispatcherClientContainerRuntime"/> →
    /// <c>POST /v1/containers/{id}/probe-from-host</c>; the dispatcher
    /// executes the host-side GET and returns the boolean result.
    /// </remarks>
    internal async Task<bool> WaitForA2AReadyAsync(
        string containerId,
        Uri endpoint,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var agentCardUri = new Uri(endpoint, ".well-known/agent.json").ToString();
        var attempts = 0;
        Exception? lastException = null;

        while (!cts.Token.IsCancellationRequested)
        {
            attempts++;
            try
            {
                var healthy = await containerRuntime.ProbeHttpFromHostAsync(
                    containerId, agentCardUri, cts.Token);
                if (healthy)
                {
                    _logger.LogDebug(
                        "A2A endpoint {Endpoint} ready after {Attempts} attempt(s) (container {ContainerId})",
                        endpoint, attempts, containerId);
                    return true;
                }
                _logger.LogDebug(
                    "A2A readiness probe attempt {Attempt} for {Endpoint} returned not-ready (container {ContainerId})",
                    attempts, endpoint, containerId);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Internal CancelAfter fired mid-probe: fall through to the
                // "did not become ready" warning + return false so the
                // timeout stays visible in logs. Outer cancellation still
                // propagates because the when-filter doesn't match.
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
                _logger.LogDebug(
                    "A2A readiness probe attempt {Attempt} for {Endpoint} failed: {Reason}",
                    attempts, endpoint, ex.Message);
            }

            try
            {
                await Task.Delay(ReadinessProbeInterval, cts.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogWarning(
            "A2A endpoint {Endpoint} did not become ready after {Attempts} attempt(s) within {Timeout} (container {ContainerId}). Last error: {LastError}",
            endpoint, attempts, timeout, containerId, lastException?.Message ?? "(none)");
        return false;
    }

    /// <summary>
    /// Serialises an <see cref="AgentDefinition"/> to YAML for the
    /// <c>/spring/context/agent-definition.yaml</c> file (D1 spec § 2.2.2).
    /// Uses underscore_case field names so the Python SDK's <c>yaml.safe_load</c>
    /// round-trips cleanly with the spec's example payload.
    /// </summary>
    private static string SerialiseAgentDefinitionYaml(AgentDefinition definition)
    {
        var doc = new
        {
            agent_id = definition.AgentId,
            name = definition.Name,
            instructions = definition.Instructions,
            execution = definition.Execution is null ? null : new
            {
                tool = definition.Execution.Tool,
                image = definition.Execution.Image,
                hosting = definition.Execution.Hosting.ToString().ToLowerInvariant(),
                provider = definition.Execution.Provider,
                model = definition.Execution.Model,
                concurrent_threads = definition.Execution.ConcurrentThreads,
            },
        };
        return _yamlSerializer.Serialize(doc);
    }

    /// <summary>
    /// Serialises a minimal tenant-config JSON for the
    /// <c>/spring/context/tenant-config.json</c> file (D1 spec § 2.2.2).
    /// The OSS platform has no separate tenant-config blob; the tenant id
    /// is the only tenant-level datum available at launch time.
    /// </summary>
    private static string SerialiseTenantConfigJson(string tenantId)
    {
        return JsonSerializer.Serialize(new { tenant_id = tenantId });
    }

    internal static SvMessage? MapA2AResponseToMessage(
        SvMessage originalMessage,
        A2AResponse response)
    {
        string output;
        int exitCode;

        // A2A v0.3 collapses the v1 PayloadCase oneof into a discriminator-based
        // class hierarchy: A2AResponse is the base, AgentTask / AgentMessage are
        // the only concrete subtypes the SDK can deliver from `message/send`.
        switch (response)
        {
            case AgentTask task:
                exitCode = task.Status.State is TaskState.Completed ? 0 : 1;
                output = ExtractTextFromTask(task);
                break;

            case AgentMessage msg:
                exitCode = 0;
                output = ExtractTextFromParts(msg.Parts);
                break;

            default:
                exitCode = 1;
                output = "No response from A2A agent.";
                break;
        }

        // AgentActor.TryReadDispatchExit reads `Error` from the payload to
        // surface the failure text in the ErrorOccurred activity event when
        // ExitCode != 0. Mirror the agent's text into Error so a Failed task
        // doesn't render as a blank "Container exit code 1: " in the activity
        // log — the message body is the only signal we have about why the
        // agent's workflow failed (e.g. dapr-agents loop error, MCP timeout).
        var payload = exitCode == 0
            ? JsonSerializer.SerializeToElement(new
            {
                Output = output,
                ExitCode = exitCode,
            })
            : JsonSerializer.SerializeToElement(new
            {
                Output = output,
                ExitCode = exitCode,
                Error = output,
            });

        return new SvMessage(
            Id: Guid.NewGuid(),
            From: originalMessage.To,
            To: originalMessage.From,
            Type: MessageType.Domain,
            ThreadId: originalMessage.ThreadId,
            Payload: payload,
            Timestamp: DateTimeOffset.UtcNow);
    }

    private static string ExtractTextFromTask(AgentTask task)
    {
        // First try artifacts
        if (task.Artifacts is { Count: > 0 })
        {
            var artifactText = string.Join("\n", task.Artifacts
                .SelectMany(a => (IEnumerable<Part>?)a.Parts ?? [])
                .OfType<TextPart>()
                .Select(p => p.Text));
            if (!string.IsNullOrEmpty(artifactText))
            {
                return artifactText;
            }
        }

        // Fall back to status message
        if (task.Status.Message is { } statusMsg)
        {
            return ExtractTextFromParts(statusMsg.Parts);
        }

        // Fall back to history
        if (task.History is { Count: > 0 })
        {
            var lastAgent = task.History.LastOrDefault(m => m.Role == MessageRole.Agent);
            if (lastAgent is not null)
            {
                return ExtractTextFromParts(lastAgent.Parts);
            }
        }

        return string.Empty;
    }

    private static string ExtractTextFromParts(IReadOnlyList<Part>? parts)
    {
        if (parts is null or { Count: 0 })
        {
            return string.Empty;
        }

        // V0_3 Parts is a polymorphic list; only TextPart has a `Text` field.
        // Other kinds (FilePart, DataPart) are intentionally dropped here —
        // the platform message protocol only carries plain text today.
        return string.Join("\n", parts
            .OfType<TextPart>()
            .Select(p => p.Text));
    }
}