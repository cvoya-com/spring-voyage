// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.Execution;

using System.Text.Json;

using A2A.V0_3;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Identifiers;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Runtime;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Connectors;
using Cvoya.Spring.Dapr.Prompts;

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
    IEnumerable<IAgentRuntimeLauncher> launchers,
    IRuntimeCatalog runtimeCatalog,
    IAgentContextBuilder agentContextBuilder,
    ITenantContext tenantContext,
    IOrchestrationToolProvider orchestrationToolProvider,
    PersistentAgentRegistry persistentAgentRegistry,
    EphemeralAgentRegistry ephemeralAgentRegistry,
    ContainerLifecycleManager containerLifecycleManager,
    AgentVolumeManager volumeManager,
    IOptions<DaprSidecarOptions> daprSidecarOptions,
    IA2ATransportFactory transportFactory,
    ICallbackTokenIssuer callbackTokenIssuer,
    IConnectorRuntimeContextResolver connectorRuntimeContextResolver,
    IConnectorPromptContextResolver connectorPromptContextResolver,
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
    // ADR-0039 D3: orchestration tool descriptors are resolved per-invocation
    // and threaded through AgentLaunchContext so launchers can attach them
    // to the runtime container. The provider returns an empty array for
    // leaf agents.
    private readonly IOrchestrationToolProvider _orchestrationToolProvider = orchestrationToolProvider
        ?? throw new ArgumentNullException(nameof(orchestrationToolProvider));
    // ADR-0038: launchers are keyed on the catalogue runtime entry's
    // launcher strategy id. The dispatcher resolves an AgentRuntime from
    // IRuntimeCatalog using the agent's persisted execution.agent slot,
    // then picks the launcher whose Kind equals the catalogue
    // entry's `launcher` field.
    private readonly Dictionary<string, IAgentRuntimeLauncher> _launchersByKind =
        launchers.ToDictionary(l => l.Kind, StringComparer.OrdinalIgnoreCase);
    private readonly IRuntimeCatalog _runtimeCatalog = runtimeCatalog
        ?? throw new ArgumentNullException(nameof(runtimeCatalog));
    private readonly ICallbackTokenIssuer _callbackTokenIssuer = callbackTokenIssuer
        ?? throw new ArgumentNullException(nameof(callbackTokenIssuer));
    // #2380: per-launch connector runtime-context contribution seam. The
    // resolver walks the subject's direct + inherited bindings and merges
    // each connector's IConnectorRuntimeContextContributor output. Plain-
    // text credentials never leave this code path (the resolver is invoked
    // here only — no API / portal surface ever calls it).
    private readonly IConnectorRuntimeContextResolver _connectorRuntimeContextResolver = connectorRuntimeContextResolver
        ?? throw new ArgumentNullException(nameof(connectorRuntimeContextResolver));
    // #2442: per-launch connector prompt-context resolver. Walks the same
    // bindings as the runtime-context resolver and returns the ordered
    // list of markdown fragments the prompt assembler renders under the
    // platform-layer "Connector context" subsection.
    private readonly IConnectorPromptContextResolver _connectorPromptContextResolver = connectorPromptContextResolver
        ?? throw new ArgumentNullException(nameof(connectorPromptContextResolver));

    internal const string CallbackTokenPayloadField = "callbackToken";

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
                $"Agent '{agentId}' has no execution configuration; " +
                "set ai.agent (the runtime registry id) in the agent / unit YAML.");
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

        var (kind, launcher) = ResolveLauncher(definition.Execution.AgentRuntimeId, agentId);

        if (mcpServer.Endpoint is null)
        {
            throw new SpringException("MCP server has not been started; endpoint is unavailable.");
        }

        var threadId = message.ThreadId
            ?? throw new SpringException("A2A dispatch requires a thread id on the message.");

        // #2442: resolve every connector binding (direct + inherited) for the
        // dispatch subject and gather the ordered list of platform-layer
        // prompt fragments each contributor produced. The fragments land in
        // the platform layer of the assembled prompt — the assembler is
        // singleton-safe so we thread them in through the per-invocation
        // PromptAssemblyContext rather than via a constructor dependency.
        var connectorPromptFragments = await _connectorPromptContextResolver.ResolveAsync(
            message.To, cancellationToken);
        var contextWithConnectorPrompts = WithConnectorPromptFragments(context, connectorPromptFragments);

        var prompt = await promptAssembler.AssembleAsync(message, contextWithConnectorPrompts, cancellationToken);
        // Carry the receiver's scheme into the MCP session so platform
        // tools (#2231) can answer get_self()-style queries without a DB
        // lookup. message.To.Scheme is the authoritative caller-kind here.
        var session = mcpServer.IssueSession(agentId, threadId, message.To.Scheme);

        // #1321: serialise AgentDefinition → YAML for the /spring/context/
        // agent-definition.yaml file (D1 spec § 2.2.2). Tenant config is
        // delivered as a minimal JSON with the current tenant id — the OSS
        // platform has no separate tenant-config blob.
        var agentDefinitionYaml = SerialiseAgentDefinitionYaml(definition);
        var tenantId = _tenantContext.CurrentTenantId;
        var tenantConfigJson = SerialiseTenantConfigJson(tenantId);

        // ADR-0039 D3: resolve the orchestration tools available to this agent
        // address on this thread. Threads them into the launch context so
        // launchers (D4–D7) can attach them to the runtime's tool surface.
        // For leaf agents the provider returns an empty array. The provider's
        // contract takes a Guid thread id; today's wire form is a free-form
        // string — when it is not Guid-shaped (e.g. legacy callers, synthetic
        // test threads) we pass Guid.Empty so the provider's per-thread hook
        // sees a deterministic value rather than a parse failure.
        var threadGuid = Guid.TryParse(threadId, out var parsedThreadGuid)
            ? parsedThreadGuid
            : Guid.Empty;
        var orchestrationTools = _orchestrationToolProvider.GetOrchestrationTools(message.To, threadGuid);

        var launchContext = new AgentLaunchContext(
            AgentId: agentId,
            ThreadId: threadId,
            Prompt: prompt,
            McpEndpoint: mcpServer.Endpoint,
            McpToken: session.Token,
            TenantId: tenantId,
            // #2251: forward the agent's owning unit id so launchers can pass
            // it to ILlmCredentialResolver — without this the resolver skips
            // Tier 1 (unit) and the parent-chain walk.
            UnitId: definition.UnitId,
            AgentDefinitionYaml: agentDefinitionYaml,
            TenantConfigJson: tenantConfigJson,
            Provider: definition.Execution.Provider,
            Model: definition.Execution.Model,
            // D3a: populate D1-spec metadata so the context builder can mint the
            // full bootstrap bundle (env vars + /spring/context/ files) per § 2.
            ConcurrentThreads: definition.Execution.ConcurrentThreads,
            OrchestrationTools: orchestrationTools,
            AgentAddress: message.To,
            CallbackThreadId: threadGuid,
            MessageId: message.Id);

        // D3a: assemble the IAgentContext bootstrap bundle (env vars + mounted
        // context files) defined by the D1 spec § 2. The bundle is merged into
        // the launcher's spec so every container receives the canonical env var
        // set regardless of tool.
        var bootstrapContext = await _agentContextBuilder.BuildAsync(launchContext, cancellationToken);

        // #2380: resolve every connector binding (direct + inherited) for the
        // dispatch subject and gather each contributor's env-vars + context
        // files. The resolver enforces the SPRING_CONNECTOR_<SLUG>_* env-var
        // namespace and the connectors/<slug>/* file sub-path so this merge
        // is a pure overlay.
        var connectorContext = await _connectorRuntimeContextResolver.ResolveAsync(
            message.To, cancellationToken);

        var spec = await launcher.PrepareAsync(launchContext, cancellationToken);

        // D3a: merge bootstrap env vars on top of launcher-produced env vars;
        // merge context files from the bootstrap bundle into the spec. The
        // builder's env vars win on collision (they are the D1-canonical names).
        var specWithContext = MergeBootstrapContext(spec, bootstrapContext);

        // #2380: merge connector contributions on top of the bootstrap-merged
        // spec, failing fast on any collision with platform-bootstrap names.
        var specWithConnectors = MergeConnectorContext(specWithContext, connectorContext);

        // D3c: provision the per-agent workspace volume before starting the
        // container. The volume survives container restarts and mid-flight
        // crashes — only ephemeral completion (ReleaseAsync) triggers
        // reclamation, per ADR-0029 § "Durable state: a per-agent persistent
        // volume".
        var volumeName = await volumeManager.EnsureAsync(agentId, cancellationToken);
        var volumeMount = AgentVolumeManager.BuildVolumeMount(volumeName);
        var specWithVolume = specWithConnectors with
        {
            ExtraVolumeMounts = MergeVolumeMounts(specWithConnectors.ExtraVolumeMounts, volumeMount),
        };

        var baseConfig = ContainerConfigBuilder.Build(definition.Execution.Image, specWithVolume);
        var useDaprSidecar = string.Equals(
            kind, LauncherIds.SpringVoyageAgent, StringComparison.OrdinalIgnoreCase);

        string? containerId = null;
        string? sidecarId = null;
        string? lifecycleNetworkName = null;
        EphemeralAgentLease? lease = null;
        try
        {
            if (useDaprSidecar)
            {
                // spring-voyage + spring-voyages 1.x: the Python process needs a
                // daprd with the delegated component profile, placement, and
                // scheduler so the DurableAgent workflow loop can start (see
                // ADR 0028 V2 interim dual-attach deployment).
                var daprAppId = BuildEphemeralDaprAppId();
                var daprConfig = baseConfig with
                {
                    DaprAppId = daprAppId,
                    DaprAppPort = spec.A2APort,
                    DaprSidecarComponentsPath = ResolveDelegatedComponentsPath(
                        definition.Execution.Provider, agentId),
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

            return await SendA2AMessageAsync(
                endpoint,
                agentId,
                containerId,
                message,
                prompt,
                callbackToken: null,
                cancellationToken);
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
    /// Merges the connector runtime-context contribution (#2380) into the
    /// launcher spec on top of the bootstrap-merged values. Fails fast when
    /// a contributed env-var key or context-file sub-path already exists in
    /// the spec — connector contributions are reserved-namespace by contract
    /// (<c>SPRING_CONNECTOR_*</c> / <c>connectors/&lt;slug&gt;/</c>), so any
    /// collision at this stage is a platform-bootstrap conflict and a real
    /// wiring bug.
    /// </summary>
    internal static AgentLaunchSpec MergeConnectorContext(
        AgentLaunchSpec spec,
        ConnectorRuntimeContextContribution connectorContext)
    {
        if (connectorContext is null
            || (connectorContext.EnvironmentVariables.Count == 0
                && connectorContext.ContextFiles.Count == 0))
        {
            return spec;
        }

        var mergedEnv = new Dictionary<string, string>(spec.EnvironmentVariables, StringComparer.Ordinal);
        foreach (var kvp in connectorContext.EnvironmentVariables)
        {
            if (mergedEnv.ContainsKey(kvp.Key))
            {
                throw new SpringException(
                    $"Connector runtime env-var '{kvp.Key}' collides with a platform-bootstrap value " +
                    "already present on the launch spec. Connector contributions are reserved to the " +
                    "SPRING_CONNECTOR_* namespace; a collision here indicates a wiring bug.");
            }
            mergedEnv[kvp.Key] = kvp.Value;
        }

        Dictionary<string, string> mergedContext;
        if (spec.ContextFiles is { Count: > 0 })
        {
            mergedContext = new Dictionary<string, string>(spec.ContextFiles, StringComparer.Ordinal);
        }
        else
        {
            mergedContext = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        foreach (var kvp in connectorContext.ContextFiles)
        {
            if (mergedContext.ContainsKey(kvp.Key))
            {
                throw new SpringException(
                    $"Connector context file '{kvp.Key}' collides with a platform-bootstrap file already " +
                    "present on the launch spec. Connector contributions are reserved to the " +
                    "connectors/<slug>/* sub-path; a collision here indicates a wiring bug.");
            }
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
    /// Returns a copy of <paramref name="context"/> with
    /// <see cref="PromptAssemblyContext.ConnectorPromptFragments"/>
    /// populated for the dispatch (#2442). Builds a minimal placeholder
    /// context when the upstream caller passed <c>null</c> so the
    /// platform-layer "Connector context" subsection still renders even
    /// when no per-invocation context was supplied. Returns the original
    /// reference (without an allocation) when there are no fragments to
    /// inject and the caller already passed a context.
    /// </summary>
    private static PromptAssemblyContext? WithConnectorPromptFragments(
        PromptAssemblyContext? context,
        IReadOnlyList<string> fragments)
    {
        if (fragments is null || fragments.Count == 0)
        {
            return context;
        }

        if (context is null)
        {
            return new PromptAssemblyContext(
                Policies: null,
                Skills: null,
                PriorMessages: [],
                LastCheckpoint: null,
                AgentInstructions: null,
                ConnectorPromptFragments: fragments);
        }

        return context with { ConnectorPromptFragments = fragments };
    }

    /// <summary>
    /// Produces a stable, short Dapr <c>app-id</c> for a persistent
    /// <c>spring-voyage</c> so workflow / actor state can survive process restarts.
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

    /// <summary>
    /// Resolves the per-dispatch Dapr components directory bind-mounted into
    /// the daprd sidecar at <c>/components</c>. The base
    /// <see cref="DaprSidecarOptions.DelegatedSpringVoyageAgentComponentsPath"/>
    /// ships every supported provider's Conversation YAML side-by-side
    /// (<c>llm-anthropic.yaml</c>, <c>llm-google.yaml</c>, <c>llm-ollama.yaml</c>,
    /// <c>llm-openai.yaml</c>); loading the whole directory means daprd
    /// fatal-exits on the first component whose secretKeyRef cannot be
    /// satisfied (e.g. <c>llm-anthropic</c> without an
    /// <c>ANTHROPIC_API_KEY</c>) — even when the dispatched unit's
    /// provider is something else entirely.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Resolution rule: <c>&lt;base&gt;/profiles/&lt;provider&gt;</c>. The
    /// repo ships <c>eng/dapr/components/delegated-spring-voyage-agent/profiles/&lt;provider&gt;/</c>
    /// with the matching LLM YAML plus the shared <c>secretstore.yaml</c> /
    /// <c>statestore.yaml</c>; daprd loads only those, leaving the other
    /// providers' components on disk but never touched. The provider
    /// comes from <see cref="AgentExecutionConfig.Provider"/>
    /// (lower-cased); missing provider returns the base path verbatim
    /// for back-compat with units that predate the profile layout.
    /// </para>
    /// <para>
    /// No <c>Directory.Exists</c> check here: this code runs inside the
    /// worker container, which does not have the host components
    /// directory mounted (the dispatcher does, on the host). The string
    /// is passed to the dispatcher as a bind-mount source; podman
    /// surfaces a clean error if the host path doesn't exist.
    /// </para>
    /// </remarks>
    private string? ResolveDelegatedComponentsPath(string? provider, string agentId)
    {
        var basePath = _daprSidecarOptions.DelegatedSpringVoyageAgentComponentsPath;
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            _logger.LogDebug(
                "Agent {AgentId} has no execution.provider; mounting legacy single-profile components dir {Path}.",
                agentId, basePath);
            return basePath;
        }

        var providerKey = provider.Trim().ToLowerInvariant();
        var profilePath = System.IO.Path.Combine(basePath, "profiles", providerKey);
        _logger.LogDebug(
            "Agent {AgentId} provider '{Provider}': mounting per-provider components profile {ProfilePath}.",
            agentId, providerKey, profilePath);
        return profilePath;
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

        // Eager validation: the persistent dispatch path mints a per-message
        // callback token scoped to the message's thread id. A malformed
        // thread id (no parse, no Guid shape) cannot be issued as a token,
        // and there is no point taking any expensive action (container
        // probes, restarts, registry mutations) before failing on it.
        // Mirrors the same check IssuePerMessageCallbackToken does — kept
        // explicit at the top so the pre-flight probe (#2092) does not get
        // a chance to flip a healthy entry to Unhealthy on a caller error.
        if (string.IsNullOrWhiteSpace(message.ThreadId) ||
            !GuidFormatter.TryParse(message.ThreadId, out _))
        {
            throw new SpringException(
                $"Message '{message.Id:N}' has malformed thread id '{message.ThreadId ?? "(null)"}'; " +
                "persistent A2A dispatch cannot issue a scoped SPRING_CALLBACK_TOKEN.");
        }

        // Check if the agent service is already running and healthy.
        var entry = await persistentAgentRegistry.TryGetAsync(agentId, cancellationToken);
        var entryHealthy = entry is not null && entry.HealthStatus == AgentHealthStatus.Healthy;

        if (entryHealthy)
        {
            // #2092: fast pre-flight HTTP probe to catch a container that
            // crashed between the registry's last health-tick and now. The
            // registry already polls every second after #2092 but that still
            // leaves a sub-second window during which a freshly-crashed
            // container would surface as a long A2A timeout instead of a
            // clean restart. The probe is short-bounded (sub-second) so a
            // healthy container pays only ~tens of ms of latency.
            var preflightOk = await persistentAgentRegistry.ProbeLivenessAsync(
                entry!, PersistentAgentRegistry.DispatchPreflightProbeTimeout, cancellationToken);

            if (!preflightOk)
            {
                _logger.LogWarning(
                    "Persistent agent {AgentId} pre-flight probe failed; " +
                    "marking unhealthy and restarting before dispatch (#2092)",
                    agentId);
                await persistentAgentRegistry.MarkUnhealthyAsync(agentId, entry!.ContainerId, cancellationToken);
                entryHealthy = false;
            }
        }

        if (entryHealthy)
        {
            endpoint = entry!.Endpoint;
            containerId = entry.ContainerId;
        }
        else
        {
            // Not running (or unhealthy) — auto-start the agent container.
            // When an unhealthy entry exists, stop the old container first so
            // we don't leak it or collide on its name (the daprd sidecar
            // pre-allocates the container name; a leaked container with the
            // same name would refuse to launch). StopContainerAsync preserves
            // the per-agent workspace volume across restart per ADR-0029.
            if (entry is not null)
            {
                _logger.LogInformation(
                    "Persistent agent {AgentId} is unhealthy; tearing down old container {ContainerId} before restart",
                    agentId, entry.ContainerId);
                await persistentAgentRegistry.StopContainerAsync(agentId, cancellationToken);
            }

            (endpoint, containerId) = await StartPersistentAgentAsync(definition, message, cancellationToken);
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

        // #2442: enrich the per-invocation context with the platform-layer
        // connector prompt fragments before handing it to the assembler.
        // Symmetric with the ephemeral path — persistent agents must see
        // the same auto-injected connector context on every dispatch.
        var connectorPromptFragments = await _connectorPromptContextResolver.ResolveAsync(
            message.To, cancellationToken);
        var contextWithConnectorPrompts = WithConnectorPromptFragments(context, connectorPromptFragments);

        var prompt = await promptAssembler.AssembleAsync(message, contextWithConnectorPrompts, cancellationToken);
        var callbackToken = IssuePerMessageCallbackToken(message);

        // #2159: register this dispatch as in flight so the background health
        // timer doesn't probe (and possibly restart) the container while the
        // agent is busy serving the A2A call. Real RPC failures are still
        // caught below and surfaced via MarkUnhealthy with the live container
        // id, so the in-flight suppression doesn't hide actual breakage.
        using var dispatchScope = persistentAgentRegistry.BeginDispatch(agentId);

        try
        {
            return await SendA2AMessageAsync(
                endpoint,
                agentId,
                containerId,
                message,
                prompt,
                callbackToken,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Container failed mid-dispatch — mark unhealthy for next dispatch.
            // Pass the container ID so a concurrent restart that already replaced
            // the container is not poisoned by a stale failure (#2159).
            _logger.LogWarning(ex,
                "A2A call to persistent agent {AgentId} failed; marking unhealthy for restart",
                agentId);
            await persistentAgentRegistry.MarkUnhealthyAsync(agentId, containerId, cancellationToken);
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
        SvMessage message,
        CancellationToken cancellationToken)
    {
        var agentId = definition.AgentId;
        var dispatchTarget = message.To;

        if (definition.Execution?.Image is null)
        {
            // #601 B-wide: same merge-aware error as the ephemeral path.
            throw new SpringException(
                $"Persistent agent '{agentId}' requires a container image. " +
                "Set execution.image on the agent (spring agent execution set --image) " +
                "or on the parent unit as a default (spring unit execution set --image).");
        }

        var (kind, launcher) = ResolveLauncher(definition.Execution.AgentRuntimeId, agentId);

        if (mcpServer.Endpoint is null)
        {
            throw new SpringException("MCP server has not been started; endpoint is unavailable.");
        }

        // Use a stable thread ID for persistent agent MCP sessions.
        var sessionId = $"persistent-{agentId}";
        var prompt = definition.Instructions ?? string.Empty;
        // Receiver's scheme threaded into the session so platform tools
        // (#2231) can resolve the caller's kind without an extra lookup.
        var session = mcpServer.IssueSession(agentId, sessionId, message.To.Scheme);

        // #1321: populate agent definition YAML + tenant config JSON for the
        // /spring/context/ mount (D1 spec § 2.2.2).
        var agentDefinitionYaml = SerialiseAgentDefinitionYaml(definition);
        var tenantId = _tenantContext.CurrentTenantId;
        var tenantConfigJson = SerialiseTenantConfigJson(tenantId);

        // ADR-0039 D3: resolve orchestration tools for the dispatch target's
        // address. Persistent dispatch uses a synthetic per-agent session id
        // rather than a real thread Guid, so we pass Guid.Empty — the
        // provider's per-thread hook is reserved for future use and currently
        // only scopes on the address.
        var orchestrationTools = _orchestrationToolProvider.GetOrchestrationTools(dispatchTarget, Guid.Empty);

        var launchContext = new AgentLaunchContext(
            AgentId: agentId,
            ThreadId: sessionId,
            Prompt: prompt,
            McpEndpoint: mcpServer.Endpoint,
            McpToken: session.Token,
            TenantId: tenantId,
            // #2251: forward the agent's owning unit id so launchers can pass
            // it to ILlmCredentialResolver — without this the resolver skips
            // Tier 1 (unit) and the parent-chain walk.
            UnitId: definition.UnitId,
            AgentDefinitionYaml: agentDefinitionYaml,
            TenantConfigJson: tenantConfigJson,
            Provider: definition.Execution.Provider,
            Model: definition.Execution.Model,
            // D3a: populate D1-spec metadata for context builder.
            ConcurrentThreads: definition.Execution.ConcurrentThreads,
            OrchestrationTools: orchestrationTools,
            AgentAddress: dispatchTarget,
            CallbackThreadId: Guid.TryParse(message.ThreadId, out var callbackThreadId)
                ? callbackThreadId
                : Guid.Empty,
            MessageId: message.Id);

        // D3a: assemble the IAgentContext bootstrap bundle (env vars + /spring/context/ files).
        var bootstrapContext = await _agentContextBuilder.BuildAsync(launchContext, cancellationToken);

        // #2380: resolve connector runtime contributions for the dispatch
        // target (direct + inherited bindings).
        var connectorContext = await _connectorRuntimeContextResolver.ResolveAsync(
            dispatchTarget, cancellationToken);

        var spec = await launcher.PrepareAsync(launchContext, cancellationToken);

        // D3a: merge bootstrap bundle into launcher spec.
        var specWithContext = MergeBootstrapContext(spec, bootstrapContext);

        // #2380: merge connector contributions; fail-fast on platform-bootstrap collisions.
        var specWithConnectors = MergeConnectorContext(specWithContext, connectorContext);

        _logger.LogInformation(
            "Starting persistent agent {AgentId} with image {Image}",
            agentId, definition.Execution.Image);

        // D3c: provision the per-agent workspace volume before starting the
        // container. For persistent agents the volume survives restarts —
        // reclamation only happens on explicit undeploy (UndeployAsync).
        var volumeName = await volumeManager.EnsureAsync(agentId, cancellationToken);
        var volumeMount = AgentVolumeManager.BuildVolumeMount(volumeName);
        var specWithVolume = specWithConnectors with
        {
            ExtraVolumeMounts = MergeVolumeMounts(specWithConnectors.ExtraVolumeMounts, volumeMount),
        };

        var baseConfig = ContainerConfigBuilder.Build(definition.Execution.Image, specWithVolume);
        var useDaprSidecar = string.Equals(
            kind, LauncherIds.SpringVoyageAgent, StringComparison.OrdinalIgnoreCase);

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
                DaprSidecarComponentsPath = ResolveDelegatedComponentsPath(
                    definition.Execution.Provider, agentId),
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

        // Register in the persistent registry. The row is the cross-process
        // source of truth (#2468) so the API host's deployment / runtime-
        // status / logs endpoints see the auto-deployed container even
        // though the write happens in the worker host.
        await persistentAgentRegistry.RegisterAsync(
            agentId, endpoint, containerId, definition, sidecarId, lifecycleNetworkName, cancellationToken);

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
        string? callbackToken,
        CancellationToken cancellationToken)
    {
        using var transport = _transportFactory.CreateTransport(containerId);
        using var httpClient = transport.CreateHttpClient(endpoint);
        var a2aClient = new A2AClient(endpoint, httpClient);

        // Map the inbound payload to the user-facing text via the shared
        // helper so all three payload shapes (bare string, { text: "…" },
        // { Task: "…" }) are handled identically across ThreadContextBuilder
        // and the A2A dispatcher. Falling back to the assembled system
        // prompt — as the previous Task-only extraction did on a miss —
        // leaked the system instructions into the user role and made the
        // agent reply to itself on follow-up turns (#2230).
        var extracted = MessagePayloadText.Extract(originalMessage.Payload);
        var userMessage = string.IsNullOrEmpty(extracted) ? prompt : extracted;

        // A2A v0.3 wire shape: MessageSendParams { message, configuration } —
        // the JSON-RPC method name is `message/send` (set by the SDK), which
        // is what the Python a2a-sdk server in the spring-voyage image expects.
        // Parts is List<Part> with derived TextPart/FilePart/DataPart; the
        // discriminator (`kind`) is set by the constructor on each subtype.
        //
        // ContextId externalization: ADR-0041 says thread.id IS the runtime's
        // session identifier. Spring Voyage stores ids in no-dash "N" form
        // (GuidFormatter), but Claude Code's `--session-id` (and other CLI
        // agents wired by the bridge) reject anything that isn't a standard
        // 8-4-4-4-12 dashed UUID. Convert at the wire boundary so internal
        // surfaces stay no-dash while external CLIs see the form they expect.
        // The response path uses originalMessage.ThreadId (not the wire echo),
        // so the inbound mapping is unaffected.
        var contextIdWire = GuidFormatter.TryParse(originalMessage.ThreadId, out var threadGuid)
            ? GuidFormatter.FormatExternal(threadGuid)
            : originalMessage.ThreadId;
        var request = new MessageSendParams
        {
            Message = new AgentMessage
            {
                Role = MessageRole.User,
                Parts = [new TextPart { Text = userMessage }],
                MessageId = originalMessage.Id.ToString(),
                ContextId = contextIdWire,
                Metadata = BuildA2AMessageMetadata(callbackToken),
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

    private string IssuePerMessageCallbackToken(SvMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.ThreadId) ||
            !GuidFormatter.TryParse(message.ThreadId, out var threadId))
        {
            throw new SpringException(
                $"Message '{message.Id:N}' has malformed thread id '{message.ThreadId ?? "(null)"}'; " +
                "persistent A2A dispatch cannot issue a scoped SPRING_CALLBACK_TOKEN.");
        }

        return _callbackTokenIssuer.Issue(new CallbackToken(
            _tenantContext.CurrentTenantId,
            message.To,
            threadId,
            message.Id,
            // The issuer treats default ExpiresAt as "use the configured callback-token lifetime".
            ExpiresAt: default));
    }

    private static Dictionary<string, JsonElement>? BuildA2AMessageMetadata(string? callbackToken)
    {
        if (string.IsNullOrWhiteSpace(callbackToken))
        {
            return null;
        }

        return new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [CallbackTokenPayloadField] = JsonSerializer.SerializeToElement(callbackToken),
        };
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
    /// <see cref="IContainerRuntime.ProbeContainerHttpAsync"/>, which the
    /// dispatcher implements as <c>podman exec &lt;containerId&gt; curl …</c>
    /// inside the agent container's own network namespace. Per ADR 0028
    /// Decision A and #2198, exec is the only mechanism the dispatcher uses
    /// to reach into a tenant container — joining the tenant network from
    /// the dispatcher process would collapse the isolation guarantee. The
    /// agent base image installs <c>curl</c> explicitly so this primitive
    /// works on every platform-built image.
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
                var healthy = await containerRuntime.ProbeContainerHttpAsync(
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
    /// Resolves the launcher for the supplied agent-runtime registry id.
    /// Surfaces a clean <see cref="SpringException"/> at every fail point —
    /// unknown runtime, runtime present but no matching launcher — so the
    /// dispatcher and the persistent-lifecycle path produce consistent error
    /// messages for operators.
    /// </summary>
    /// <remarks>
    /// #1732: this is the single derivation point for the launcher. The
    /// agent definition stores the runtime id (<c>agent</c>), the
    /// catalogue maps it to a
    /// <see cref="Cvoya.Spring.Core.Catalog.AgentRuntime.Launcher"/>
    /// id, and the launcher dictionary is keyed on the same value. Two
    /// distinct runtimes that share a launcher id (e.g. <c>openai</c>
    /// and <c>google</c> both reference <c>spring-voyage-agent</c>)
    /// resolve to the same launcher.
    /// </remarks>
    internal (string Kind, IAgentRuntimeLauncher Launcher) ResolveLauncher(
        string agentRuntimeId,
        string agentId)
    {
        var runtime = _runtimeCatalog.GetAgentRuntime(agentRuntimeId)
            ?? throw new SpringException(
                $"No agent runtime is registered in eng/runtime-catalog/runtime-catalog.yaml with id " +
                $"'{agentRuntimeId}' (agent '{agentId}'). Add the entry to the catalogue or " +
                "set ai.runtime to a registered runtime id.");

        if (!_launchersByKind.TryGetValue(runtime.Launcher, out var launcher))
        {
            throw new SpringException(
                $"No IAgentRuntimeLauncher registered for launcher strategy id " +
                $"'{runtime.Launcher}' (agent runtime '{agentRuntimeId}', agent '{agentId}').");
        }

        return (runtime.Launcher, launcher);
    }

    /// <summary>
    /// Serialises an <see cref="AgentDefinition"/> to YAML for the
    /// <c>/spring/context/agent-definition.yaml</c> file (D1 spec § 2.2.2).
    /// Uses underscore_case field names so the Python SDK's <c>yaml.safe_load</c>
    /// round-trips cleanly with the spec's example payload.
    /// </summary>
    private string SerialiseAgentDefinitionYaml(AgentDefinition definition)
    {
        // ADR-0038: emit the derived launcher strategy id so containers
        // can see which in-container engine was selected for this turn.
        // Sourced from the catalogue's `launcher` field on the runtime
        // entry.
        string? kind = null;
        if (definition.Execution is not null)
        {
            var runtime = _runtimeCatalog.GetAgentRuntime(definition.Execution.AgentRuntimeId);
            kind = runtime?.Launcher;
        }

        var doc = new
        {
            agent_id = definition.AgentId,
            name = definition.Name,
            instructions = definition.Instructions,
            execution = definition.Execution is null ? null : new
            {
                agent = definition.Execution.AgentRuntimeId,
                kind = kind,
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
    private static string SerialiseTenantConfigJson(Guid tenantId)
    {
        return JsonSerializer.Serialize(new { tenant_id = Cvoya.Spring.Core.Identifiers.GuidFormatter.Format(tenantId) });
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
        // agent's workflow failed (e.g. spring-voyages loop error, MCP timeout).
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
