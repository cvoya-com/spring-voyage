// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.Agents;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Catalog;
using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.ModelProviders;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Runtime;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Agents;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Capabilities;
using Cvoya.Spring.Dapr.Configuration;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Initiative;
using Cvoya.Spring.Dapr.Mcp;
using Cvoya.Spring.Dapr.Prompts;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

/// <summary>
/// Execution and agent registrations: LLM dispatch, AI providers, prompt
/// assembly, agent runtimes, container runtime, dispatchers, MCP server,
/// and hosted execution services.
/// </summary>
internal static class ServiceCollectionExtensionsExecution
{
    internal static IServiceCollection AddCvoyaSpringExecution(
        this IServiceCollection services,
        SpringHostRole role)
    {
        var isDocGen = BuildEnvironment.IsDesignTimeTooling;

        // Options
        services.AddOptions<AiProviderOptions>().BindConfiguration(AiProviderOptions.SectionName);
        // ContainerRuntimeOptions used to be bound here too. Stage 2 of #522
        // moved it to the dispatcher exclusively — the worker does not call
        // a container CLI any more (DaprSidecarManager and
        // ContainerLifecycleManager now route through IContainerRuntime).
        // The Dapr sidecar image / health knobs that used to share that
        // section moved to DaprSidecarOptions ("Dapr:Sidecar").
        services.AddOptions<DaprSidecarOptions>().BindConfiguration(DaprSidecarOptions.SectionName);
        services.AddOptions<DispatcherClientOptions>().BindConfiguration(DispatcherClientOptions.SectionName);
        services.AddOptions<CallbackTokenOptions>().BindConfiguration(CallbackTokenOptions.SectionName);
        // #2586: agent-facing runtime callback base URL — the API
        // host's agent-reachable URL. DispatcherCallbackEnvironmentBuilder
        // stamps it onto every runtime container as SPRING_CALLBACK_URL.
        services.AddOptions<CallbackBaseUrlOptions>()
            .BindConfiguration(CallbackBaseUrlOptions.SectionName);
        services.AddOptions<UnitRuntimeOptions>().BindConfiguration(UnitRuntimeOptions.SectionName);

        // LLM dispatch seam (ADR 0028 Decision E / #1168) — IAiProvider
        // implementations talk to a normal HttpClient; the primary
        // HttpMessageHandler underneath is LlmHttpMessageHandler, which
        // routes through ILlmDispatcher so the cloud overlay (or an OSS
        // deployment that has moved Ollama off spring-net) can swap the
        // transport without touching the providers. Default
        // implementation is HttpClientLlmDispatcher (direct via
        // HttpClient on a dedicated named transport that does not
        // recurse through LlmHttpMessageHandler); deployments opt into
        // the proxied path with AddCvoyaSpringDispatcherProxiedLlm.
        services.AddCvoyaSpringDirectLlmDispatcher();

        // Execution — every IAiProvider implementation talks through an
        // HttpClient whose primary handler is LlmHttpMessageHandler so the
        // call flows through ILlmDispatcher (ADR 0028 / #1168).
        //
        // Multiple IAiProvider entries are registered. The order matters:
        // GetService<IAiProvider>() returns the *last* registration, so
        // platform-level singletons that still consume IAiProvider directly
        // (Tier1CognitionProvider, Tier2CognitionProvider, …) land on the
        // OSS default — Anthropic. Unit-scoped dispatch resolves
        // IAiProviderRegistry instead and selects per-unit by the
        // manifest's execution.provider slot (#1696).
        services.AddHttpClient<IAiProvider, OllamaProvider>()
            .ConfigurePrimaryHttpMessageHandler(static sp =>
                new LlmHttpMessageHandler(sp.GetRequiredService<ILlmDispatcher>()));
        services.AddHttpClient<IAiProvider, AnthropicProvider>()
            .ConfigurePrimaryHttpMessageHandler(static sp =>
                new LlmHttpMessageHandler(sp.GetRequiredService<ILlmDispatcher>()));

        // Registry that lets unit-scoped consumers resolve a provider by
        // the unit's declared execution.provider value. Singleton: the
        // index it builds at construction is immutable. TryAdd so the
        // private cloud overlay can substitute a tenant-aware
        // implementation without touching this registration.
        services.TryAddSingleton<IAiProviderRegistry, AiProviderRegistry>();
        services.AddSingleton<IPromptAssembler, PromptAssembler>();
        services.AddSingleton<IPlatformPromptProvider, PlatformPromptProvider>();

        // Agent observation / initiative-dispatch coordinator (#1276).
        // Singleton: stateless across agents; uses per-call delegates for all
        // state access. TryAdd so the private cloud repo can layer a
        // tenant-aware decorator without touching this registration.
        services.TryAddSingleton<IAgentObservationCoordinator, AgentObservationCoordinator>();

        // Agent lifecycle / activation coordinator (concern 7 of #1276).
        // Singleton: stateless across agents; uses per-call delegates for
        // StateManager access and the optional IExpertiseSeedProvider. TryAdd
        // so the private cloud repo can layer a tenant-aware coordinator
        // (e.g. one that adds audit logging on every seeding event).
        services.TryAddSingleton<IAgentLifecycleCoordinator, AgentLifecycleCoordinator>();

        // Agent thread-mailbox coordinator (#1335 / #1276 concern 2).
        // Singleton: stateless across agents; all actor-state reads and writes
        // flow through per-call delegates so no Dapr actor types are captured.
        // TryAdd so the private cloud repo can substitute a tenant-aware
        // implementation without touching this registration.
        services.TryAddSingleton<IAgentMailboxCoordinator, AgentMailboxCoordinator>();

        // Agent metadata/state coordinator (#1339 / #1276 concern 6).
        // #2048 / ADR-0040: agent live config (model / specialty / enabled
        // / execution_mode), skill grants, and expertise are EF-backed.
        // The singleton store wraps the scoped EF repository so AgentActor
        // (not request-scoped) can read / write through it. TryAddSingleton
        // so the cloud overlay can layer audit / cross-tenant guards on top.
        services.TryAddSingleton<IAgentLiveConfigStore, AgentLiveConfigStore>();

        // Singleton: stateless across agents; the metadata / skills /
        // expertise reads + writes flow through IAgentLiveConfigStore (#2048).
        // TryAdd so the private cloud repo can substitute a tenant-aware
        // implementation (e.g. one that layers audit logging or gates skill
        // assignment on per-tenant allowlists) without touching this registration.
        services.TryAddSingleton<IAgentStateCoordinator, AgentStateCoordinator>();

        // Agent execution-dispatch coordinator (#1336 / #1276 concern 3).
        // Singleton: stateless across agents; IExecutionDispatcher and
        // MessageRouter are injected as singleton constructor parameters;
        // activity-emission and active-thread-clearing flow through
        // per-call delegates so no Dapr actor types are captured.
        // ADR-0052 / #2618: this coordinator is consumed only by the actor
        // runtime-invocation pipeline and transitively depends on the
        // execution-host-only IExecutionDispatcher, so it registers in the
        // role-gated block below — the HTTP front door never resolves it.

        // Agent amendment coordinator (#1337 / #1276 concern 4).
        // Singleton: stateless across agents; IUnitMembershipRepository and
        // the actor's CancellationTokenSource flow through per-call delegates
        // so no scoped services or Dapr actor types are captured. TryAdd so
        // the private cloud repo can layer tenant-aware amendment handling
        // (e.g. rate-limiting, audit logging) without touching this registration.
        services.TryAddSingleton<IAgentAmendmentCoordinator, AgentAmendmentCoordinator>();

        // Agent unit-policy coordinator (#1338 / #1276 concern 5).
        // Singleton: stateless across agents; IUnitPolicyEnforcer is scoped
        // and is passed as per-call delegate parameters (evaluateModel /
        // evaluateCost / resolveExecutionMode) so the singleton coordinator
        // does not capture a scoped service. TryAdd so the private cloud repo
        // can substitute a tenant-aware implementation (e.g. one that adds
        // audit logging or additional policy dimensions).
        services.TryAddSingleton<IAgentUnitPolicyCoordinator, AgentUnitPolicyCoordinator>();

        // ADR-0038: services consult IRuntimeCatalog directly. An empty
        // fallback IRuntimeCatalog stays registered here only when the
        // host omits AddCvoyaSpringRuntimeCatalog (test harnesses that
        // exercise the DI graph without the catalogue project) so
        // dependent services can resolve.
        services.TryAddSingleton<IRuntimeCatalog>(_ => new EmptyRuntimeCatalog());

        // Tier-2 LLM credential resolver (#615). Delegates to the
        // existing ISecretResolver (Unit → Tenant inheritance, ADR 0003).
        // Credentials must be set at tenant or unit scope — there is no
        // env-variable fallback. TryAdd so the private cloud host can
        // substitute a tenant-scoped implementation (per-tenant Key
        // Vault, BYOK) without forking the registration.
        //
        // ISecretResolver is registered as Scoped (ComposedSecretResolver
        // uses the scoped SpringDbContext via EfSecretRegistry), so the
        // credential resolver inherits that scope.
        services.TryAddScoped<ILlmCredentialResolver, LlmCredentialResolver>();

        // Container runtime. The worker no longer holds the local container
        // binary; the spring-dispatcher service does. The worker binds a
        // single DispatcherClientContainerRuntime that forwards every call to
        // the dispatcher over HTTP. See docs/architecture/deployment.md
        // ("Dispatcher service") and issue #513. TryAdd so downstream
        // deployments that run the dispatcher in-process (test harnesses,
        // alternative topologies) can pre-register their own IContainerRuntime.
        // ADR-0052 / #2627: IContainerRuntime is execution-host-only — only
        // the worker's container-lifecycle / unit-teardown surface consumes
        // it; registered in the role-gated block below.
        services.AddDispatcherHttpClient();
        // ADR-0051: the per-turn callback JWT is no longer the messaging
        // credential — sv.messaging.* is served by the platform MCP server
        // under the MCP session token, gated like every other sv.* tool. The
        // issuer and the per-tenant signing key are retained because the
        // OTLP-ingest auth plane (OtlpCallbackAuthHandler) still mints and
        // validates this token shape as its credential; migrating OTLP off it
        // is a tracked follow-up.
        services.TryAddSingleton<ITenantSigningKeyProvider, DispatcherClientTenantSigningKeyProvider>();
        services.TryAddSingleton<ICallbackTokenIssuer, CallbackTokenIssuer>();
        services.TryAddSingleton<IAgentCallbackEnvironmentBuilder, DispatcherCallbackEnvironmentBuilder>();

        // ADR-0052 / #2627: the unit-container-teardown surface
        // (IContainerRuntime, IDaprSidecarManager, ContainerLifecycleManager,
        // IUnitContainerLifecycle) and the A2A transport / agent-context
        // builder are execution-host-only — only the worker's runtime
        // launchers, dispatcher, and the internal unit-teardown route consume
        // them. They are registered in the role-gated block below so the HTTP
        // front door registers zero execution services; UnitEndpoints'
        // force-delete path delegates unit-container teardown to the worker
        // over IExecutionHostGateway.

        services.AddOptions<AgentContextOptions>().BindConfiguration(AgentContextOptions.SectionName);

        // Agent definition is owned by Dapr (DB-backed). The four
        // IAgentRuntimeLauncher strategies (ClaudeCodeLauncher, CodexLauncher,
        // GeminiLauncher, SpringVoyageAgentLauncher) moved into
        // Cvoya.Spring.AgentRuntimes per ADR-0038 Chunk 2a; their DI
        // registration is via AddCvoyaSpringAgentRuntimes() in the host
        // composition root.
        services.TryAddSingleton<IAgentDefinitionProvider, DbAgentDefinitionProvider>();

        // ADR-0039 §6 — execution-config inheritance resolver. The
        // default reads each parent unit's persisted defaults via
        // IUnitExecutionStore and intersects them per field; the cloud
        // overlay can substitute a tenant-aware variant via the standard
        // TryAdd* seam (e.g. one that consults tenant-default fallthrough
        // for top-level agents, or layers audit logging on conflict).
        services.TryAddSingleton<IExecutionConfigInheritanceResolver, ExecutionConfigInheritanceResolver>();
        // D3c: per-agent workspace volume manager, the persistent / ephemeral
        // agent registries, and the imperative PersistentAgentLifecycle
        // (powering deploy/scale/logs/undeploy — #396) are all
        // execution-host-only (ADR-0052 / #2618) — registered in the
        // role-gated block below. The API host delegates the operator surface
        // to the worker over IExecutionHostGateway.

        // #2336 / Sub C of #2332. The introspector fetches the agent's
        // image-tier tool definitions from its /a2a/tools endpoint at
        // deploy / image-rotation time and caches them onto
        // agent_definitions.image_tools / unit_definitions.image_tools.
        // Named HttpClient so cloud overlays can layer interceptors
        // (telemetry, mTLS) without touching the introspector.
        // TryAdd so a test fixture or cloud overlay can swap the
        // implementation.
        services.AddHttpClient(HttpAgentToolsIntrospector.HttpClientName);
        services.TryAddSingleton<IAgentToolsIntrospector, HttpAgentToolsIntrospector>();

        // In-process MCP server. ADR-0052 / Wave 3 (#2625): the MCP surface is
        // served as a minimal-API route on the worker's existing Kestrel host
        // — the route + Kestrel-endpoint wiring lives in the worker host.
        // McpServer is no longer an IHostedService and no longer owns a port
        // listener; it owns the session store and the JSON-RPC dispatch only.
        // ADR-0052 / #2618: the McpServer / IMcpServer singletons are
        // execution-host-only — only the dispatcher (worker-side) issues and
        // validates sessions, and the surface is a worker Kestrel route.
        services.AddOptions<McpServerOptions>().BindConfiguration(McpServerOptions.SectionName);

        // ADR-0052 / #2618, #2627: the HTTP front door delegates persistent-
        // agent deploy / undeploy / scale / deployment-status / logs and
        // unit-container teardown to the execution host over Dapr service
        // invocation. The gateway depends only on DaprClient — no execution
        // singleton — so it registers on both hosts; the API host resolves it
        // from AgentEndpoints / UnitEndpoints in place of the execution
        // singletons.
        services.TryAddSingleton<IExecutionHostGateway, DaprExecutionHostGateway>();

        // ADR-0052 / #2618, #2627: every execution service belongs to the
        // execution host only. The HTTP front door used to resolve the
        // persistent-agent singletons (PersistentAgentLifecycle /
        // PersistentAgentRegistry / the McpServer) and the unit-container
        // teardown surface (IContainerRuntime / IDaprSidecarManager /
        // ContainerLifecycleManager / IUnitContainerLifecycle) because
        // AgentEndpoints / UnitEndpoints called into them in-process. Wave 3
        // (#2618) and #2627 make those endpoints delegate to the worker over
        // IExecutionHostGateway, so the front-door composition
        // registers none of these — AddCvoyaSpringDapr(HttpFrontDoor)
        // registers zero execution services. The execution-host (worker)
        // graph still gets every one. Gated unconditionally — not behind
        // isDocGen — because the goal is that the front door never registers
        // them at all.
        if (role == SpringHostRole.ExecutionHost)
        {
            // Container-lifecycle / unit-teardown surface (#2627). TryAdd so
            // downstream deployments that run the dispatcher in-process (test
            // harnesses, alternative topologies) or want direct-HTTP A2A can
            // pre-register their own IContainerRuntime / IA2ATransportFactory.
            services.TryAddSingleton<IContainerRuntime, DispatcherClientContainerRuntime>();
            services.AddSingleton<IDaprSidecarManager, DaprSidecarManager>();
            services.AddSingleton<ContainerLifecycleManager>();
            services.TryAddSingleton<IUnitContainerLifecycle, UnitContainerLifecycle>();
            // D2 / Stage 2 of ADR-0029: A2A transport seam. The default
            // implementation routes every outbound A2A POST through the
            // dispatcher proxy (DispatcherProxyA2ATransport) when a container
            // id is available, and falls back to direct-HTTP
            // (DirectA2ATransport) when it is not.
            services.TryAddSingleton<IA2ATransportFactory, DispatcherProxyA2ATransportFactory>();
            services.TryAddSingleton<IAgentContextBuilder, AgentContextBuilder>();

            services.TryAddSingleton<IExecutionDispatcher, A2AExecutionDispatcher>();
            services.TryAddSingleton<IAgentDispatchCoordinator, AgentDispatchCoordinator>();
            services.TryAddSingleton<AgentVolumeManager>();
            services.TryAddSingleton<PersistentAgentRegistry>();
            services.TryAddSingleton<EphemeralAgentRegistry>();
            services.TryAddSingleton<PersistentAgentLifecycle>();
            // ADR-0052 §2: the McpServer / IMcpServer singletons are
            // execution-host-only — the dispatcher (worker-side) is the sole
            // session authority and the surface is a worker Kestrel route.
            services.TryAddSingleton<McpServer>();
            services.TryAddSingleton<IMcpServer>(sp => sp.GetRequiredService<McpServer>());

            if (!isDocGen)
            {
                // The execution hosted services that supervise
                // delegated-execution containers start only on the
                // execution host (spring-worker).
                services.AddHostedService(sp => sp.GetRequiredService<AgentVolumeManager>());
                services.AddHostedService(sp => sp.GetRequiredService<PersistentAgentRegistry>());
                services.AddHostedService(sp => sp.GetRequiredService<EphemeralAgentRegistry>());
                // Container health gauge (#1378): polls GetHealthAsync for every tracked
                // persistent-agent container and emits spring.container.healthy via
                // System.Diagnostics.Metrics (BCL Meter, MeterName = "Cvoya.Spring.Dapr").
                services.AddHostedService<ContainerHealthMetricsService>();
            }
        }

        return services;
    }

    /// <summary>
    /// DI-fallback empty <see cref="IRuntimeCatalog"/>. Used only when no
    /// real catalogue is wired (test harnesses). Production hosts always
    /// register the YAML-backed catalogue via
    /// <c>AddCvoyaSpringRuntimeCatalog()</c> before <c>AddCvoyaSpringDapr</c>;
    /// <c>TryAddSingleton</c> guarantees the real registration wins.
    /// </summary>
    private sealed class EmptyRuntimeCatalog : IRuntimeCatalog
    {
        public IReadOnlyList<Cvoya.Spring.Core.Catalog.AgentRuntime> AgentRuntimes => Array.Empty<Cvoya.Spring.Core.Catalog.AgentRuntime>();

        public IReadOnlyList<ModelProvider> ModelProviders => Array.Empty<ModelProvider>();

        public Cvoya.Spring.Core.Catalog.AgentRuntime? GetAgentRuntime(string id) => null;

        public ModelProvider? GetModelProvider(string id) => null;
    }
}
