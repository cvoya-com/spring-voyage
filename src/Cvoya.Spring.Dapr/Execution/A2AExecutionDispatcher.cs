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
using Cvoya.Spring.Core.Messaging.Rendering;
using Cvoya.Spring.Core.Messaging.Rendering.Renderers;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Core.Runtime;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Dapr.Connectors;
using Cvoya.Spring.Dapr.Prompts;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;


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
///   <item>Observe how the runtime terminated and return a <see cref="RuntimeOutcome"/>.</item>
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
/// <para>
/// ADR-0056: the dispatcher's contract is <em>"run the runtime; tell me how
/// it terminated"</em>. Stdout, A2A task replies, and file-capture buffers
/// are captured as a <see cref="RuntimeOutcome.ReasoningTrace"/> for
/// diagnostics — they are never synthesised into messages routed back to
/// the original sender. Every observable effect a runtime has on the
/// outside world flows through platform tool calls (<c>sv.messaging.*</c>,
/// <c>sv.task.*</c>, …).
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
    PersistentAgentRegistry persistentAgentRegistry,
    EphemeralAgentRegistry ephemeralAgentRegistry,
    ContainerLifecycleManager containerLifecycleManager,
    AgentVolumeManager volumeManager,
    IOptions<DaprSidecarOptions> daprSidecarOptions,
    IA2ATransportFactory transportFactory,
    IConnectorRuntimeContextResolver connectorRuntimeContextResolver,
    IConnectorPromptContextResolver connectorPromptContextResolver,
    Cvoya.Spring.Dapr.Prompts.IInboundEnvelopeResolver inboundEnvelopeResolver,
    IMessagePayloadRendererRegistry payloadRenderers,
    ILoggerFactory loggerFactory) : IExecutionDispatcher
{
    private readonly Cvoya.Spring.Dapr.Prompts.IInboundEnvelopeResolver _inboundEnvelopeResolver =
        inboundEnvelopeResolver ?? throw new ArgumentNullException(nameof(inboundEnvelopeResolver));

    // #2856: A2A reasoning-trace text is now resolved through the canonical
    // Message.Payload renderer registry (#2843), so the dispatcher's
    // ExtractTextFromTask / ExtractTextFromParts helpers were retired. The
    // dispatcher wraps the A2A response in a Message-shaped envelope
    // (kind: "a2a.task" / "a2a.message") and the renderer registry picks
    // the A2aTaskPayloadRenderer that walks artifacts → status → history.
    private readonly IMessagePayloadRendererRegistry _payloadRenderers =
        payloadRenderers ?? throw new ArgumentNullException(nameof(payloadRenderers));

    private readonly ILogger _logger = loggerFactory.CreateLogger<A2AExecutionDispatcher>();
    private readonly DaprSidecarOptions _daprSidecarOptions = daprSidecarOptions.Value;
    private readonly IA2ATransportFactory _transportFactory = transportFactory
        ?? throw new ArgumentNullException(nameof(transportFactory));
    private readonly IAgentContextBuilder _agentContextBuilder = agentContextBuilder
        ?? throw new ArgumentNullException(nameof(agentContextBuilder));
    private readonly ITenantContext _tenantContext = tenantContext
        ?? throw new ArgumentNullException(nameof(tenantContext));
    // ADR-0038: launchers are keyed on the catalogue runtime entry's
    // launcher strategy id. The dispatcher resolves an AgentRuntime from
    // IRuntimeCatalog using the agent's persisted execution.runtime slot,
    // then picks the launcher whose Kind equals the catalogue
    // entry's `launcher` field.
    private readonly Dictionary<string, IAgentRuntimeLauncher> _launchersByKind =
        launchers.ToDictionary(l => l.Kind, StringComparer.OrdinalIgnoreCase);
    private readonly IRuntimeCatalog _runtimeCatalog = runtimeCatalog
        ?? throw new ArgumentNullException(nameof(runtimeCatalog));
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
    public async Task<RuntimeOutcome> DispatchAsync(
        SvMessage inboundMessage,
        PromptAssemblyContext? context,
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Dispatching A2A execution for message {MessageId} to {Destination}",
            inboundMessage.Id, inboundMessage.To);

        var agentId = inboundMessage.To.Path;
        var definition = await agentDefinitionProvider.GetByIdAsync(agentId, ct)
            ?? throw new SpringException($"No agent definition found for '{agentId}'.");

        if (definition.Execution is null)
        {
            throw new SpringException(
                $"Agent '{agentId}' has no execution configuration; " +
                "set ai.agent (the runtime registry id) in the agent / unit YAML.");
        }

        return definition.Execution.Hosting switch
        {
            AgentHostingMode.Persistent => await DispatchPersistentAsync(inboundMessage, definition, context, ct),
            AgentHostingMode.Ephemeral => await DispatchEphemeralAsync(inboundMessage, definition, context, ct),
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

    private async Task<RuntimeOutcome> DispatchEphemeralAsync(
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

        var (kind, launcher) = ResolveLauncher(definition.Execution.Runtime, agentId);

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

        var prompt = await promptAssembler.AssembleAsync(contextWithConnectorPrompts, cancellationToken);
        // Carry the receiver's scheme into the MCP session so platform
        // tools (#2231) can answer get_self()-style queries without a DB
        // lookup. message.To.Scheme is the authoritative caller-kind here.
        // ADR-0051: the inbound message id rides on the session so the
        // sv.messaging.* tools carry the per-turn delivery authority the
        // retired callback JWT used to provide.
        var session = mcpServer.IssueSession(agentId, threadId, message.To.Scheme, message.Id);

        var tenantId = _tenantContext.CurrentTenantId;

        // ADR-0051: sv.messaging.* tools are served by the single platform
        // MCP server alongside every other sv.* tool — the launcher no longer
        // needs a per-invocation messaging tool list. CallbackThreadId is kept
        // Guid-shaped for the OTLP-ingest callback token; a non-Guid wire form
        // (legacy callers, synthetic test threads) falls back to Guid.Empty.
        var threadGuid = Guid.TryParse(threadId, out var parsedThreadGuid)
            ? parsedThreadGuid
            : Guid.Empty;

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
            Provider: definition.Execution.Model?.Provider,
            Model: definition.Execution.Model?.Id,
            // D3a: populate D1-spec metadata so the context builder can mint the
            // full bootstrap bundle (env vars) per § 2.
            ConcurrentThreads: definition.Execution.ConcurrentThreads,
            AgentAddress: message.To,
            CallbackThreadId: threadGuid,
            MessageId: message.Id,
            // #2691 / #2667: resolved system_prompt_mode. The agent → unit
            // cascade happens in DbAgentDefinitionProvider.Merge; the final
            // fallback to Append is applied here so launchers consume a
            // single concrete value.
            SystemPromptMode: definition.Execution.SystemPromptMode
                ?? Cvoya.Spring.Core.Catalog.SystemPromptMode.Append);

        // D3a: assemble the IAgentContext bootstrap bundle (env vars + mounted
        // context files) defined by the D1 spec § 2. The bundle is merged into
        // the launcher's spec so every container receives the canonical env var
        // set regardless of tool.
        var bootstrapContext = await _agentContextBuilder.BuildAsync(launchContext, cancellationToken);

        // #2380: resolve every connector binding (direct + inherited) for the
        // dispatch subject and gather each contributor's env-vars + context
        // files. The resolver enforces the SPRING_CONNECTOR_<SLUG>_* env-var
        // namespace and the .spring/connectors/<slug>/* file sub-path so this
        // merge is a pure overlay.
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
        var volumeMount = AgentVolumeManager.BuildVolumeMount(volumeName, agentId);
        var specWithVolume = specWithConnectors with
        {
            ExtraVolumeMounts = MergeVolumeMounts(specWithConnectors.ExtraVolumeMounts, volumeMount),
            // Default the container's working directory to the per-member
            // workspace mount (the documented behaviour on
            // AgentLaunchSpec.WorkingDirectory). The launcher's override
            // wins when it sets one explicitly. CLI launchers that
            // discover config files (e.g. Claude Code's `.mcp.json`)
            // relative to CWD rely on this default.
            WorkingDirectory = specWithConnectors.WorkingDirectory
                ?? AgentWorkspaceContract.BuildMountPathNoSlash(agentId),
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
                        definition.Execution.Model?.Provider, agentId),
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

            // ADR-0052 §4: deliver the per-turn session token in the A2A
            // message/send metadata so token delivery is uniform across the
            // ephemeral and persistent dispatch paths. (The ephemeral
            // container's launch-time `.mcp.json` already carries this same
            // per-turn token, so the bridge's rewrite is a no-op-equivalent
            // here — kept uniform deliberately.)
            return await SendA2AMessageAsync(
                endpoint,
                agentId,
                containerId,
                message,
                session.Token,
                cancellationToken);
        }
        finally
        {
            // ADR-0056 §5: the tool-call count is read off the session
            // BEFORE revocation so the dispatch coordinator can decide
            // whether to emit RuntimeCompletedSilent. SendA2AMessageAsync
            // already snapshotted it onto the outcome — revoking here is
            // safe.
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
        // ADR-0055: context files no longer flow through this merge — they
        // ride the agent bootstrap bundle the sidecar pulls from the worker.
        var mergedEnv = new Dictionary<string, string>(spec.EnvironmentVariables, StringComparer.Ordinal);
        foreach (var kvp in bootstrap.EnvironmentVariables)
        {
            mergedEnv[kvp.Key] = kvp.Value;
        }

        return spec with
        {
            EnvironmentVariables = mergedEnv,
        };
    }

    /// <summary>
    /// Merges the connector runtime-context contribution (#2380) into the
    /// launcher spec on top of the bootstrap-merged values. Fails fast when
    /// a contributed env-var key already exists in the spec — connector
    /// contributions are reserved to the <c>SPRING_CONNECTOR_*</c>
    /// namespace, so any collision at this stage is a platform-bootstrap
    /// conflict and a real wiring bug.
    /// </summary>
    /// <remarks>
    /// Per ADR-0055 the connector's per-binding context files are now part
    /// of the bootstrap bundle, written by the sidecar under the
    /// <c>.spring/connectors/&lt;slug&gt;/</c> sub-path of the workspace (the
    /// <c>.spring/</c> namespace ADR-0058 reserves for platform-controlled
    /// files). This merge handles only the env-var contribution.
    /// </remarks>
    internal static AgentLaunchSpec MergeConnectorContext(
        AgentLaunchSpec spec,
        ConnectorRuntimeContextContribution connectorContext)
    {
        if (connectorContext is null
            || connectorContext.EnvironmentVariables.Count == 0)
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

        return spec with
        {
            EnvironmentVariables = mergedEnv,
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

    private async Task<RuntimeOutcome> DispatchPersistentAsync(
        SvMessage message,
        AgentDefinition definition,
        PromptAssemblyContext? context,
        CancellationToken cancellationToken)
    {
        var agentId = definition.AgentId;
        Uri endpoint;
        string? containerId;

        // Eager validation: the persistent dispatch path stamps the message's
        // thread id into the launcher env contract. A malformed thread id (no
        // parse, no Guid shape) is a caller error — fail before taking any
        // expensive action (container probes, restarts, registry mutations) so
        // the pre-flight probe (#2092) does not flip a healthy entry to
        // Unhealthy on a caller error.
        if (string.IsNullOrWhiteSpace(message.ThreadId) ||
            !GuidFormatter.TryParse(message.ThreadId, out _))
        {
            throw new SpringException(
                $"Message '{message.Id:N}' has malformed thread id '{message.ThreadId ?? "(null)"}'; " +
                "persistent A2A dispatch requires a Guid-shaped thread id.");
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

        // #2669: the previous block here resolved connector prompt
        // fragments and ran IPromptAssembler.AssembleAsync, then
        // discarded the resulting string — persistent-agent system-prompt
        // delivery moved to the bootstrap-bundle endpoint
        // (AgentBootstrapBundleProvider) long ago. The ephemeral path
        // (DispatchEphemeralAsync) still calls these because its prompt
        // flows into AgentLaunchContext.Prompt for ContributeBundleAsync;
        // the persistent container fetches the same bundle on cold-start
        // through the worker-side endpoint, so re-resolving here was pure
        // wasted work plus connector-resolver side effects on every
        // persistent dispatch.

        // #2159: register this dispatch as in flight so the background health
        // timer doesn't probe (and possibly restart) the container while the
        // agent is busy serving the A2A call. Real RPC failures are still
        // caught below and surfaced via MarkUnhealthy with the live container
        // id, so the in-flight suppression doesn't hide actual breakage.
        using var dispatchScope = persistentAgentRegistry.BeginDispatch(agentId);

        // ADR-0052 §4: every persistent dispatch — warm container or
        // cold start — issues exactly one per-turn MCP session and
        // delivers the token in the A2A message/send (§4). The launch
        // path no longer issues a launch-time session (StartPersistentAgentAsync),
        // so this is the only session the container ever sees. The
        // session is scoped to the real per-turn thread id + message id
        // (the same arguments the ephemeral path passes), NOT the stable
        // `persistent-{agentId}` container-identity thread. message.To.Scheme
        // carries the receiver's kind so platform tools (#2231) can resolve
        // the caller's kind without an extra lookup; message.Id rides on the
        // session so sv.messaging.* tools carry per-turn delivery authority
        // (ADR-0051). The session is revoked in the finally below at turn end,
        // matching the ephemeral path.
        var session = mcpServer.IssueSession(
            agentId, message.ThreadId!, message.To.Scheme, message.Id);

        RuntimeOutcome outcome;
        try
        {
            outcome = await SendA2AMessageAsync(
                endpoint,
                agentId,
                containerId,
                message,
                session.Token,
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
        finally
        {
            mcpServer.RevokeSession(session.Token);
        }

        // #2519: a successful A2A POST is the strongest possible "this
        // container is alive" signal. Bump the runtime row's UpdatedAt so
        // sibling host processes' health-sweep freshness gate skips an
        // otherwise-scheduled restart against this still-busy agent. Failures
        // are not heartbeats — the catch block above already marked the row
        // Unhealthy without bumping UpdatedAt. Heartbeat is detached from
        // the caller's cancellation token: a successful dispatch must record
        // its own freshness signal even if the upstream actor turn was
        // cancelled after we got the response.
        await persistentAgentRegistry.RecordDispatchHeartbeatAsync(agentId, CancellationToken.None);
        return outcome;
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

        var (kind, launcher) = ResolveLauncher(definition.Execution.Runtime, agentId);

        if (mcpServer.Endpoint is null)
        {
            throw new SpringException("MCP server has not been started; endpoint is unavailable.");
        }

        // The persistent agent's container-identity thread id. Stable
        // across restarts so launcher-emitted env vars / config files
        // carry a coherent thread id between launch and the first turn.
        var sessionId = $"persistent-{agentId}";
        var prompt = definition.Instructions ?? string.Empty;

        // ADR-0052 §3/§4: the launch path no longer issues an MCP
        // session. A deploy/auto-start is a launch with no turn context —
        // there is no per-turn threadId / messageId to scope a session to,
        // and a session minted here would be revoked before the first real
        // turn anyway. The freshly-started container has no usable MCP token
        // until the turn's message/send delivers one (DispatchPersistentAsync
        // issues the per-turn session and the TypeScript bridge writes it into
        // .mcp.json before spawning the CLI). The launch-time .mcp.json
        // carries the endpoint but an empty token. McpEndpoint is still set
        // from the live worker-side McpServer so the container knows where to
        // dial once it has a token.

        var tenantId = _tenantContext.CurrentTenantId;

        var launchContext = new AgentLaunchContext(
            AgentId: agentId,
            ThreadId: sessionId,
            Prompt: prompt,
            McpEndpoint: mcpServer.Endpoint,
            // ADR-0052 §3/§4: no launch-time session — the container has
            // no usable token until its first turn's message/send.
            McpToken: string.Empty,
            TenantId: tenantId,
            // #2251: forward the agent's owning unit id so launchers can pass
            // it to ILlmCredentialResolver — without this the resolver skips
            // Tier 1 (unit) and the parent-chain walk.
            UnitId: definition.UnitId,
            Provider: definition.Execution.Model?.Provider,
            Model: definition.Execution.Model?.Id,
            // D3a: populate D1-spec metadata for context builder.
            ConcurrentThreads: definition.Execution.ConcurrentThreads,
            AgentAddress: dispatchTarget,
            CallbackThreadId: Guid.TryParse(message.ThreadId, out var callbackThreadId)
                ? callbackThreadId
                : Guid.Empty,
            MessageId: message.Id,
            // #2691 / #2667: resolved system_prompt_mode. Cascade has
            // already landed on definition.Execution via the definition
            // provider's agent → unit merge; final fallback is Append.
            SystemPromptMode: definition.Execution.SystemPromptMode
                ?? Cvoya.Spring.Core.Catalog.SystemPromptMode.Append);

        // D3a: assemble the IAgentContext bootstrap bundle (env vars).
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
        var volumeMount = AgentVolumeManager.BuildVolumeMount(volumeName, agentId);
        var specWithVolume = specWithConnectors with
        {
            ExtraVolumeMounts = MergeVolumeMounts(specWithConnectors.ExtraVolumeMounts, volumeMount),
            // Default the container's working directory to the per-member
            // workspace mount (the documented behaviour on
            // AgentLaunchSpec.WorkingDirectory). The launcher's override
            // wins when it sets one explicitly.
            WorkingDirectory = specWithConnectors.WorkingDirectory
                ?? AgentWorkspaceContract.BuildMountPathNoSlash(agentId),
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
                    definition.Execution.Model?.Provider, agentId),
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
    internal async Task<RuntimeOutcome> SendA2AMessageAsync(
        Uri endpoint,
        string agentId,
        string? containerId,
        SvMessage originalMessage,
        string mcpToken,
        CancellationToken cancellationToken)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var transport = _transportFactory.CreateTransport(containerId);
        using var httpClient = transport.CreateHttpClient(endpoint);
        var a2aClient = new A2AClient(endpoint, httpClient);

        // #2746 — wrap the inbound payload in the structured envelope so
        // the runtime sees `from`, `to` (the participants the sender
        // targeted), `message_id`, `timestamp`, and the payload as
        // first-class fields plus a fenced JSON appendix for parseable
        // payloads (webhook events, custom shapes). The envelope makes the
        // "this is a structured event to act on" framing visible at the
        // input boundary so the runtime cannot drift into "answer this
        // chat turn as text" (the silent-runtime-on-casual-input failure
        // pattern #2703 documented). The empty-payload fallback used to
        // collapse to an empty user message; the envelope still names
        // sender/message_id so the runtime always has handles to act on.
        var userMessage = await _inboundEnvelopeResolver
            .RenderEnvelopeAsync(originalMessage, cancellationToken);

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
                // ADR-0052 §4: deliver the per-turn MCP session token in the
                // message/send metadata under `mcpToken`. The TypeScript
                // agent-sidecar bridge reads this field and rewrites the
                // `spring-voyage` MCP server block's Authorization header in
                // `.mcp.json` before spawning the CLI, so every turn dials the
                // worker-side McpServer with a token it actually issued for
                // that turn. The session is revoked at turn end by the
                // dispatch path that issued it. The field is intentionally a
                // clean, accurately-named `mcpToken` — it is the MCP session
                // token, not the retired callback JWT.
                Metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                {
                    ["mcpToken"] = JsonSerializer.SerializeToElement(mcpToken),
                },
            },
            Configuration = new MessageSendConfiguration
            {
                AcceptedOutputModes = ["text/plain"],
            },
        };

        // #2718: track the latest task we observed so the finally below
        // can `tasks/cancel` it if we exit on a non-terminal state. Without
        // that, the caller's outer `finally` runs `RevokeSession` while the
        // agent's CLI is still running, every subsequent MCP call returns
        // 401, and the model effectively stalls mid-turn.
        AgentTask? lastTask = null;
        try
        {
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
            if (response is AgentTask initialTask)
            {
                lastTask = initialTask;
                if (!IsTerminalTaskState(initialTask.Status.State))
                {
                    response = await PollTaskUntilTerminalAsync(
                        a2aClient, initialTask, agentId, containerId, cancellationToken);
                    if (response is AgentTask polled)
                    {
                        lastTask = polled;
                    }
                }
            }

            sw.Stop();
            // ADR-0056 §5: snapshot the tool-call count BEFORE the caller
            // revokes the MCP session so the dispatch coordinator can decide
            // RuntimeCompletedSilent without racing the revoke.
            var toolCallCount = mcpServer.GetToolCallCount(mcpToken);
            return MapA2AResponseToOutcome(response, sw.Elapsed, toolCallCount, agentId, containerId, _payloadRenderers);
        }
        finally
        {
            // #2718: any non-terminal exit — poll timeout, exception,
            // OperationCanceledException — leaves the bridge with a running
            // CLI. The caller's outer `finally` (in
            // DispatchPersistentAsync / DispatchEphemeralAsync) is about to
            // run RevokeSession on the MCP session this turn was issued.
            // Best-effort tell the bridge to tear the CLI down first so the
            // worker's view ("turn done") matches the container's view ("CLI
            // exited"). The cancel is bounded (the bridge's SIGTERM grace is
            // small) and any error is swallowed — we are already on the
            // failure path.
            if (lastTask is not null && !IsTerminalTaskState(lastTask.Status.State))
            {
                await TryCancelAgentTaskAsync(a2aClient, lastTask.Id, agentId, containerId);
            }
        }
    }

    /// <summary>
    /// Maximum time the dispatcher will wait for a best-effort
    /// <c>tasks/cancel</c> roundtrip when bailing out of a non-terminal turn.
    /// The bridge's own cancellation grace is small (SIGTERM → SIGKILL,
    /// typically ~5 s); a longer cap here would block the caller's
    /// <see cref="IMcpServer.RevokeSession"/> for no benefit. The token
    /// passed to <see cref="A2AClient.CancelTaskAsync"/> is detached from
    /// the caller's cancellation token because we want the cancel to fire
    /// precisely when the caller has already given up.
    /// </summary>
    internal static readonly TimeSpan CancelTaskBudget = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Effective <c>tasks/cancel</c> budget. Tests override this to exercise
    /// the timeout branch without real wall-clock sleep.
    /// </summary>
    internal TimeSpan EffectiveCancelTaskBudget = CancelTaskBudget;

    /// <summary>
    /// Best-effort <c>tasks/cancel</c> on the bridge so the spawned CLI
    /// is torn down before the caller's <c>finally</c> revokes the per-turn
    /// MCP session. Bounded by <see cref="EffectiveCancelTaskBudget"/>;
    /// every failure mode (timeout, transport error, bridge rejection) is
    /// logged and swallowed — we are already in the dispatch's give-up path
    /// and must not throw past the caller's finally.
    /// </summary>
    private async Task TryCancelAgentTaskAsync(
        A2AClient a2aClient,
        string taskId,
        string agentId,
        string? containerId)
    {
        using var cts = new CancellationTokenSource(EffectiveCancelTaskBudget);
        try
        {
            await a2aClient.CancelTaskAsync(new TaskIdParams { Id = taskId }, cts.Token);
            _logger.LogInformation(
                "Best-effort A2A tasks/cancel issued for task {TaskId} (agent {AgentId} container {ContainerId}) — bridge will SIGTERM the CLI before the dispatch's finally revokes the MCP session.",
                taskId, agentId, containerId);
        }
        catch (Exception ex)
        {
            // Bridge may already be gone (container torn down), the cancel
            // may have raced the bridge's own exit, or the bridge may have
            // returned -32001 because the task already finished. All of
            // these are acceptable — RevokeSession follows immediately.
            _logger.LogWarning(ex,
                "Best-effort A2A tasks/cancel for task {TaskId} (agent {AgentId} container {ContainerId}) failed; the agent CLI may outlive the per-turn MCP session — see #2718.",
                taskId, agentId, containerId);
        }
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
    /// Translates an <see cref="A2AResponse"/> into a
    /// <see cref="RuntimeOutcome"/>. Per
    /// <see href="../../../docs/decisions/0056-tool-only-side-effects.md">ADR-0056</see>
    /// the terminal text the runtime produced becomes the
    /// <see cref="RuntimeOutcome.ReasoningTrace"/> — it is diagnostic only
    /// and is never synthesised into a <see cref="SvMessage"/>. The host
    /// stops inferring intent from terminal text; runtimes that want to
    /// reply on the thread call <c>sv.messaging.send</c>.
    /// </summary>
    /// <remarks>
    /// #2856: the A2A response is wrapped into a <see cref="SvMessage"/>-shaped
    /// envelope (<c>kind: "a2a.task"</c> / <c>"a2a.message"</c>) and routed
    /// through <see cref="IMessagePayloadRendererRegistry"/>, so the timeline,
    /// Slack outbound, and A2A reasoning-trace paths all share one extractor.
    /// The dispatcher still reads task id / state directly off the typed A2A
    /// SDK objects for the diagnostics dictionary — those are not text and
    /// don't belong in the renderer registry. Static so callers that need
    /// raw mapping for a specific renderer set (e.g. bridge-wire contract
    /// tests) can call it without standing up a full dispatcher.
    /// </remarks>
    internal static RuntimeOutcome MapA2AResponseToOutcome(
        A2AResponse response,
        TimeSpan duration,
        int toolCallCount,
        string agentId,
        string? containerId,
        IMessagePayloadRendererRegistry payloadRenderers)
    {
        ArgumentNullException.ThrowIfNull(payloadRenderers);

        string? reasoningTrace;
        int exitCode;
        string? a2aTaskId = null;
        string? a2aTaskState = null;

        // A2A v0.3 collapses the v1 PayloadCase oneof into a discriminator-based
        // class hierarchy: A2AResponse is the base, AgentTask / AgentMessage are
        // the only concrete subtypes the SDK can deliver from `message/send`.
        switch (response)
        {
            case AgentTask task:
                exitCode = task.Status.State is TaskState.Completed ? 0 : 1;
                reasoningTrace = RenderA2AResponseAsText(BuildA2aTaskPayload(task), payloadRenderers);
                a2aTaskId = task.Id;
                a2aTaskState = task.Status.State.ToString();
                break;

            case AgentMessage msg:
                exitCode = 0;
                reasoningTrace = RenderA2AResponseAsText(BuildA2aMessagePayload(msg), payloadRenderers);
                break;

            default:
                exitCode = 1;
                reasoningTrace = null;
                break;
        }

        var diagnostics = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [RuntimeOutcome.ToolCallCountKey] = toolCallCount,
            ["agentId"] = agentId,
        };

        if (containerId is not null)
        {
            diagnostics["containerId"] = containerId;
        }

        if (a2aTaskId is not null)
        {
            diagnostics["a2aTaskId"] = a2aTaskId;
        }

        if (a2aTaskState is not null)
        {
            diagnostics["a2aTaskState"] = a2aTaskState;
        }

        return new RuntimeOutcome(exitCode, duration, reasoningTrace, diagnostics);
    }

    /// <summary>
    /// Wraps a built A2A payload in a synthetic <see cref="SvMessage"/> and
    /// asks the renderer registry to extract text. The synthetic envelope
    /// is purely a carrier — only <see cref="SvMessage.Payload"/> and
    /// <see cref="SvMessage.Type"/> are consulted by built-in renderers,
    /// so the other fields use harmless defaults.
    /// </summary>
    private static string? RenderA2AResponseAsText(JsonElement payload, IMessagePayloadRendererRegistry payloadRenderers)
    {
        var carrier = new SvMessage(
            Id: Guid.Empty,
            From: A2APayloadCarrierAddress,
            To: A2APayloadCarrierAddress,
            Type: MessageType.Domain,
            ThreadId: null,
            Payload: payload,
            Timestamp: DateTimeOffset.UtcNow);

        return payloadRenderers.TryRender(carrier);
    }

    /// <summary>
    /// Sentinel address used on the synthetic <see cref="SvMessage"/> carrier
    /// in <see cref="RenderA2AResponseAsText"/>. The renderer registry does
    /// not consult <see cref="SvMessage.From"/> / <see cref="SvMessage.To"/>,
    /// but the <see cref="SvMessage"/> record requires non-null
    /// <see cref="Address"/> values — using <see cref="Guid.Empty"/> on a
    /// fixed scheme makes the placeholder obvious in any diagnostic dump
    /// that ever surfaces it.
    /// </summary>
    private static readonly Address A2APayloadCarrierAddress = new("agent", Guid.Empty);

    /// <summary>
    /// Builds the pinned <c>{ kind: "a2a.task", … }</c> payload from an
    /// <see cref="AgentTask"/>: the A2A SDK's own JSON serialisation with
    /// the top-level <c>kind</c> discriminator rewritten so the renderer
    /// registry's <see cref="A2aTaskPayloadRenderer"/> recognises the wrap.
    /// </summary>
    /// <remarks>
    /// Mutating only the top-level <c>kind</c> field keeps the rest of the
    /// SDK's layout intact — artifact / status / history nodes carry the
    /// SDK-native part shape (<c>kind: "text"</c> / <c>kind: "file"</c> /
    /// <c>kind: "data"</c>) the renderer walks. Avoids a parallel hand-
    /// written serialisation that would drift from the SDK over time.
    /// </remarks>
    private static JsonElement BuildA2aTaskPayload(AgentTask task)
        => ReplaceTopLevelKind(JsonSerializer.SerializeToElement(task), A2aTaskPayloadRenderer.TaskKind);

    /// <summary>
    /// Builds the pinned <c>{ kind: "a2a.message", … }</c> payload from an
    /// <see cref="AgentMessage"/>. Mirrors <see cref="BuildA2aTaskPayload"/>:
    /// rewrites the SDK's top-level <c>kind</c> so the renderer registry
    /// picks the same <see cref="A2aTaskPayloadRenderer"/>, which walks
    /// <c>parts</c> on the simpler <c>a2a.message</c> shape.
    /// </summary>
    private static JsonElement BuildA2aMessagePayload(AgentMessage message)
        => ReplaceTopLevelKind(JsonSerializer.SerializeToElement(message), A2aTaskPayloadRenderer.MessageKind);

    private static JsonElement ReplaceTopLevelKind(JsonElement source, string kindValue)
    {
        using var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("kind", kindValue);
            foreach (var prop in source.EnumerateObject())
            {
                if (string.Equals(prop.Name, "kind", StringComparison.Ordinal))
                {
                    continue;
                }

                prop.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        buffer.Position = 0;
        using var doc = JsonDocument.Parse(buffer);
        return doc.RootElement.Clone();
    }
}
