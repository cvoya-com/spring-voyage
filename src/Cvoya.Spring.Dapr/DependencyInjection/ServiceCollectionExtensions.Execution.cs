// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.AgentRuntimes;
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
        this IServiceCollection services)
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

        // Execution — AnthropicProvider needs HttpClient. Primary
        // handler is LlmHttpMessageHandler so the call flows through
        // ILlmDispatcher.
        services.AddHttpClient<IAiProvider, AnthropicProvider>()
            .ConfigurePrimaryHttpMessageHandler(static sp =>
                new LlmHttpMessageHandler(sp.GetRequiredService<ILlmDispatcher>()));
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

        // Agent-runtime plugin registry (#678, cornerstone of the #674
        // refactor). Enumerates every DI-registered IAgentRuntime so the
        // API layer, wizard, and CLI can resolve runtimes by id without
        // importing concrete runtime packages. Per-runtime migrations
        // (#679–#682) register the concrete IAgentRuntime implementations
        // via their own AddCvoyaSpringAgentRuntime<Name>() extensions.
        // TryAdd so the private cloud host can supply a tenant-scoped
        // registry (e.g. one that filters on tenant_agent_runtime_installs)
        // without forking.
        services.TryAddSingleton<IAgentRuntimeRegistry, AgentRuntimeRegistry>();

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
        services.AddDispatcherHttpClient();
        services.TryAddSingleton<IContainerRuntime, DispatcherClientContainerRuntime>();
        services.AddSingleton<IDaprSidecarManager, DaprSidecarManager>();
        services.AddSingleton<ContainerLifecycleManager>();
        services.TryAddSingleton<IUnitContainerLifecycle, UnitContainerLifecycle>();
        // D2 / Stage 2 of ADR-0029: A2A transport seam. The default
        // implementation routes every outbound A2A POST through the
        // dispatcher proxy (DispatcherProxyA2ATransport) when a container
        // id is available, and falls back to direct-HTTP (DirectA2ATransport)
        // when it is not (test harnesses, future dual-homed deployments).
        // Private-cloud or dual-homed hosts that want direct-HTTP for all
        // containers pre-register their own IA2ATransportFactory before
        // calling AddCvoyaSpringDapr; TryAdd ensures their registration wins.
        services.TryAddSingleton<IA2ATransportFactory, DispatcherProxyA2ATransportFactory>();
        services.TryAddSingleton<IExecutionDispatcher, A2AExecutionDispatcher>();

        services.AddOptions<AgentContextOptions>().BindConfiguration(AgentContextOptions.SectionName);
        services.TryAddSingleton<IAgentContextBuilder, AgentContextBuilder>();

        // Agent definition + tool launchers used by A2AExecutionDispatcher.
        services.TryAddSingleton<IAgentDefinitionProvider, DbAgentDefinitionProvider>();
        services.AddSingleton<IAgentToolLauncher, ClaudeCodeLauncher>();
        services.AddSingleton<IAgentToolLauncher, CodexLauncher>();
        services.AddSingleton<IAgentToolLauncher, GeminiLauncher>();
        services.AddSingleton<IAgentToolLauncher, DaprAgentLauncher>();
        // D3c: per-agent workspace volume manager. Provisions volumes before
        // agent containers start, reclaims them on agent delete / ephemeral
        // completion, and emits volume-level telemetry (size, growth rate).
        // Registered as a singleton so all dispatch paths share the in-process
        // tracking map; registered as IHostedService for the metric timer.
        services.TryAddSingleton<AgentVolumeManager>();
        services.TryAddSingleton<PersistentAgentRegistry>();
        // Per-thread registry for ephemeral agent containers. PR 5 of
        // the #1087 series: the unified A2A dispatch path starts ephemeral
        // containers in detached mode and tears them down when the turn
        // drains. The registry exists so the host has a single place to
        // observe and stop ephemeral containers (and so graceful shutdown
        // sweeps anything still tracked).
        services.TryAddSingleton<EphemeralAgentRegistry>();
        // Imperative lifecycle service powering the persistent-agent CLI surface
        // (spring agent deploy/status/scale/logs/undeploy — #396). Kept separate
        // from A2AExecutionDispatcher so the turn-dispatch path stays focused on
        // "there is a message to run" and the operator surface stays focused on
        // "there is a container to manage."
        services.TryAddSingleton<PersistentAgentLifecycle>();

        // In-process MCP server — options and singleton always registered so
        // endpoints that depend on IMcpServer resolve correctly during OpenAPI
        // generation. The hosted-service registration (which binds a port and
        // starts the health monitor) is gated by doc-gen mode. See #370.
        services.AddOptions<McpServerOptions>().BindConfiguration(McpServerOptions.SectionName);
        services.TryAddSingleton<McpServer>();
        services.TryAddSingleton<IMcpServer>(sp => sp.GetRequiredService<McpServer>());

        if (!isDocGen)
        {
            services.AddHostedService(sp => sp.GetRequiredService<AgentVolumeManager>());
            services.AddHostedService(sp => sp.GetRequiredService<PersistentAgentRegistry>());
            services.AddHostedService(sp => sp.GetRequiredService<EphemeralAgentRegistry>());
            services.AddHostedService(sp => sp.GetRequiredService<McpServer>());
        }

        return services;
    }
}