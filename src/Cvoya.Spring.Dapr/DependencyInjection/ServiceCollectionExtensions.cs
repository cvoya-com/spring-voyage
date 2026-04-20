// Copyright CVOYA LLC. Licensed under the Business Source License 1.1.
// See LICENSE.md in the project root for full license terms.

namespace Cvoya.Spring.Dapr.DependencyInjection;

using Cvoya.Spring.Connectors;
using Cvoya.Spring.Core.AgentRuntimes;
using Cvoya.Spring.Core.Capabilities;
using Cvoya.Spring.Core.Cloning;
using Cvoya.Spring.Core.Configuration;
using Cvoya.Spring.Core.Costs;
using Cvoya.Spring.Core.CredentialHealth;
using Cvoya.Spring.Core.Directory;
using Cvoya.Spring.Core.Execution;
using Cvoya.Spring.Core.Initiative;
using Cvoya.Spring.Core.Messaging;
using Cvoya.Spring.Core.Observability;
using Cvoya.Spring.Core.Orchestration;
using Cvoya.Spring.Core.Policies;
using Cvoya.Spring.Core.Secrets;
using Cvoya.Spring.Core.Skills;
using Cvoya.Spring.Core.State;
using Cvoya.Spring.Core.Tenancy;
using Cvoya.Spring.Core.Units;
using Cvoya.Spring.Dapr.Actors;
using Cvoya.Spring.Dapr.AgentRuntimes;
using Cvoya.Spring.Dapr.Auth;
using Cvoya.Spring.Dapr.Capabilities;
using Cvoya.Spring.Dapr.Cloning;
using Cvoya.Spring.Dapr.Configuration;
using Cvoya.Spring.Dapr.Connectors;
using Cvoya.Spring.Dapr.Costs;
using Cvoya.Spring.Dapr.CredentialHealth;
using Cvoya.Spring.Dapr.Data;
using Cvoya.Spring.Dapr.Data.Entities;
using Cvoya.Spring.Dapr.Execution;
using Cvoya.Spring.Dapr.Initiative;
using Cvoya.Spring.Dapr.Mcp;
using Cvoya.Spring.Dapr.Observability;
using Cvoya.Spring.Dapr.Orchestration;
using Cvoya.Spring.Dapr.Prompts;
using Cvoya.Spring.Dapr.Routing;
using Cvoya.Spring.Dapr.Secrets;
using Cvoya.Spring.Dapr.Skills;
using Cvoya.Spring.Dapr.State;
using Cvoya.Spring.Dapr.Tenancy;

using global::Dapr.Actors.Client;
using global::Dapr.Workflow;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

/// <summary>
/// Extension methods for registering Spring Voyage services with the dependency injection container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers core Spring Voyage abstractions. Currently a no-op placeholder that allows
    /// the host to signal intent and provides a future extension point.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringCore(this IServiceCollection services)
    {
        return services;
    }

    /// <summary>
    /// Registers all Dapr-backed implementations for routing, execution, orchestration, and prompt assembly.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The application configuration, used to resolve the PostgreSQL connection string.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringDapr(this IServiceCollection services, IConfiguration configuration)
    {
        var isDocGen = BuildEnvironment.IsDesignTimeTooling;

        // Dapr client, actor proxy factory, and workflow client
        services.AddDaprClient();

        // Configure the actor proxy factory to use JSON serialization with
        // shared options that include a JsonElement converter which detaches
        // each parsed element from the transient JsonDocument owned by the
        // deserialization scope. Dapr's default DataContract serializer
        // cannot round-trip Message.Payload (a JsonElement) and leaves it as
        // default(JsonElement), which then crashes ASP.NET Core's response
        // writer with "Operation is not valid due to the current state of
        // the object" — the bug behind the GET /api/v1/agents/{id} 500.
        services.TryAddSingleton<IActorProxyFactory>(_ => new ActorProxyFactory(
            new ActorProxyOptions
            {
                UseJsonSerialization = true,
                JsonSerializerOptions = ActorRemotingJsonOptions.Instance,
            }));

        services.AddDaprWorkflow(options => { });

        // During build-time OpenAPI generation (GetDocument.Insider) the Dapr
        // Workflow hosted service starts a gRPC bidirectional stream with the
        // sidecar. There is no sidecar at build time, so it spams "Connection
        // refused" errors. Strip the worker (keeping DaprWorkflowClient and
        // the rest of the workflow DI graph) via the shared helper that also
        // backs the integration-test workaround for #568. See #370 and #568.
        if (isDocGen)
        {
            services.RemoveDaprWorkflowWorker();
        }

        // EF Core / PostgreSQL.
        //
        // Test harnesses (e.g. CustomWebApplicationFactory) pre-register
        // DbContextOptions<SpringDbContext> via UseInMemoryDatabase BEFORE
        // calling AddCvoyaSpringDapr; we respect that and skip our default
        // Npgsql wiring. Otherwise we bind Npgsql when a connection string
        // is present, or register the context without a provider when one
        // is not. The #616 DatabaseConfigurationRequirement owns the
        // missing / malformed classification and raises a fatal error
        // through the startup validator — we no longer throw from here.
        //
        // Design-time tooling (dotnet-ef, dotnet-getdocument for the
        // build-time OpenAPI document) loads the host without a database
        // connection and never actually opens the context. The absent
        // validator at build-time plus the provider-less registration keep
        // the build-time OpenAPI emitter working with no local database.
        var alreadyRegistered = services.Any(d =>
            d.ServiceType == typeof(DbContextOptions<SpringDbContext>));
        if (!alreadyRegistered)
        {
            var connectionString = configuration.GetConnectionString("SpringDb");
            if (string.IsNullOrEmpty(connectionString))
            {
                // Register the context without a provider so construction
                // succeeds. The DatabaseConfigurationRequirement reports
                // Invalid+Mandatory at StartAsync, aborting boot with a
                // clear message before any EF query runs. Build-time
                // tooling (isDocGen) never resolves the context.
                services.AddDbContext<SpringDbContext>(_ => { });
            }
            else
            {
                services.AddDbContext<SpringDbContext>(options =>
                    options.UseNpgsql(connectionString, npgsql =>
                        npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "spring")));
            }
        }

        // #616: tier-1 configuration validation framework. Register the
        // validator + reference requirements first so the validator's
        // IHostedService enumerates them before any other hosted service
        // runs. Design-time tooling skips the validator entirely — the
        // build-time OpenAPI emitter never starts the host lifecycle, and
        // the validator would otherwise fail on a provider-less context.
        //
        // #639 adds the subsystem requirements (Dapr state store, secrets,
        // dispatcher, container runtime) alongside the Database reference
        // requirement shipped in #616. They are registered here (rather
        // than next to each subsystem's own option binding below) so
        // AddCvoyaSpringDapr remains the single entry point that wires the
        // full validation set.
        if (!isDocGen)
        {
            services.AddCvoyaSpringConfigurationValidator();
            // Signal to DatabaseConfigurationRequirement whether the caller
            // pre-registered a DbContext (test harness path) — captured at
            // registration time to avoid resolving the scoped
            // DbContextOptions<SpringDbContext> from the root provider.
            services.AddSingleton(new DatabaseConfigurationRequirement.TestHarnessSignal(alreadyRegistered));
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IConfigurationRequirement, DatabaseConfigurationRequirement>());
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IConfigurationRequirement, DaprStateStoreConfigurationRequirement>());
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IConfigurationRequirement, SecretsConfigurationRequirement>());
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IConfigurationRequirement, DispatcherConfigurationRequirement>());
            services.TryAddEnumerable(
                ServiceDescriptor.Singleton<IConfigurationRequirement, ContainerRuntimeConfigurationRequirement>());
        }

        // Database options. Always bound — both API and Worker hosts (and
        // any private-cloud host that calls AddCvoyaSpringDapr) need to
        // read DatabaseOptions even though, by default, only the Worker
        // actually applies migrations. Migration registration itself is
        // intentionally NOT performed here: see AddCvoyaSpringDatabaseMigrator
        // and the remarks on DatabaseMigrator for why exactly one host in a
        // deployment owns migrations (issue #305).
        services.AddOptions<DatabaseOptions>().BindConfiguration(DatabaseOptions.SectionName);

        // Repositories
        services.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        services.TryAddScoped<IUnitMembershipRepository, UnitMembershipRepository>();
        services.TryAddScoped<IUnitPolicyRepository, UnitPolicyRepository>();

        // Unit-policy enforcement (#162 / #163). TryAdd so the private cloud
        // repo can pre-register a tenant-scoped / audit-logging wrapper that
        // wraps the OSS default. Scoped because the underlying repositories
        // use SpringDbContext which is scoped per request.
        services.TryAddScoped<IUnitPolicyEnforcer, DefaultUnitPolicyEnforcer>();

        // Skill bundles (#167 / C4). The resolver is a singleton — it reads
        // from disk and caches per-host. The validator is scoped because it
        // depends on IUnitPolicyRepository (which is scoped). TryAdd so the
        // cloud host can register a tenant-scoped bundle store or validator
        // without touching the API layer.
        services.AddOptions<SkillBundleOptions>().BindConfiguration(SkillBundleOptions.SectionName);
        // #687: resolve `ISkillBundleResolver` through a tenant-filtering
        // decorator so bundles surface only when the current tenant has an
        // `enabled=true` binding. The inner file-system resolver stays a
        // singleton (its cache is restart-scoped); the decorator is scoped
        // because the binding service holds a SpringDbContext.
        services.TryAddSingleton<FileSystemSkillBundleResolver>();
        services.TryAddScoped<ISkillBundleResolver, TenantFilteringSkillBundleResolver>();
        services.TryAddScoped<ITenantSkillBundleBindingService, DefaultTenantSkillBundleBindingService>();
        services.TryAddScoped<ISkillBundleValidator, DefaultSkillBundleValidator>();
        services.TryAddSingleton<IUnitSkillBundleStore, StateStoreBackedUnitSkillBundleStore>();

        // Default-tenant bootstrap seed adapter for the file-system bundle
        // resolver (#676). Registered as an enumerable ITenantSeedProvider
        // so the DefaultTenantBootstrapService picks it up on first run;
        // the wrapper is a thin enumeration that keeps the resolver in
        // the Phase 1 bootstrap loop without coupling it to an OSS bundle
        // install table that does not yet exist (Phase 2 follow-up).
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITenantSeedProvider, FileSystemSkillBundleSeedProvider>());

        // Per-tenant agent-runtime + connector install services (#683,
        // #684). Scoped because they depend on SpringDbContext; paired
        // with singleton seed providers that crack open a child DI scope
        // per seed pass. TryAdd* so a cloud overlay can register a
        // tenant-scoped variant (e.g. backed by a different repository)
        // without touching this call site.
        services.TryAddScoped<ITenantAgentRuntimeInstallService, TenantAgentRuntimeInstallService>();
        services.TryAddScoped<ITenantConnectorInstallService, TenantConnectorInstallService>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITenantSeedProvider, AgentRuntimeInstallSeedProvider>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ITenantSeedProvider, ConnectorInstallSeedProvider>());

        // Credential-health store (#686). Scoped because it holds a
        // SpringDbContext. The DelegatingHandler that feeds this store at
        // use-time opens a child DI scope per write so it can be invoked
        // from any HttpClient pipeline, regardless of ambient scope.
        services.TryAddScoped<ICredentialHealthStore, DefaultCredentialHealthStore>();

        // Agents-as-skills surface (#359 — rework of closed #532). The
        // catalog derives the skill surface live from the expertise
        // directory (#487 / #498) rather than from a startup snapshot, so
        // directory mutations (agent gains expertise, unit boundary
        // changes) propagate on the next enumeration. The invoker is the
        // protocol-agnostic seam that skill callers use instead of
        // IMessageRouter directly — the default implementation routes
        // through the bus so the boundary / permission / policy / activity
        // chain runs end-to-end; the future A2A gateway (#539) will slot in
        // here as an alternative implementation. TryAdd* so downstream
        // hosts (test harnesses, tenant-scoped wrappers, #539 gateway) can
        // pre-register their own catalog / invoker and keep it.
        services.TryAddSingleton<IExpertiseSkillCatalog, ExpertiseSkillCatalog>();
        services.TryAddSingleton<ISkillInvoker, MessageRouterSkillInvoker>();
        services.TryAddSingleton<ExpertiseSkillRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISkillRegistry, ExpertiseSkillRegistry>(
            sp => sp.GetRequiredService<ExpertiseSkillRegistry>()));

        // Directory-search meta-skill registry (#542). Advertises
        // `directory/search` alongside the `expertise/*` surface so planners
        // can call it BEFORE any other skill to resolve "I need something
        // that does X" into a concrete slug. Registered via
        // TryAddEnumerable so the cloud host can add its own search registry
        // (e.g. a tenant-scoped variant) without displacing this one.
        services.TryAddSingleton<DirectorySearchSkillRegistry>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISkillRegistry, DirectorySearchSkillRegistry>(
            sp => sp.GetRequiredService<DirectorySearchSkillRegistry>()));

        // Unit-membership backfill hosted service (#160 / C2b-1).
        // Gated by Database:BackfillMemberships; idempotent; short-lived.
        // Also gated by doc-gen mode — the service depends on SpringDbContext
        // which may not have a configured provider during build-time OpenAPI
        // generation. See #370.
        if (!isDocGen)
        {
            services.AddHostedService<UnitMembershipBackfillService>();
        }

        // Options
        services.AddOptions<AiProviderOptions>().BindConfiguration(AiProviderOptions.SectionName);
        services.AddOptions<ContainerRuntimeOptions>().BindConfiguration("ContainerRuntime");
        services.AddOptions<DispatcherClientOptions>().BindConfiguration(DispatcherClientOptions.SectionName);
        services.AddOptions<UnitRuntimeOptions>().BindConfiguration(UnitRuntimeOptions.SectionName);
        services.AddOptions<WorkflowOrchestrationOptions>().BindConfiguration("WorkflowOrchestration");

        // Routing
        services.AddSingleton<DirectoryCache>();
        services.TryAddSingleton<IDirectoryService, DirectoryService>();
        services.TryAddSingleton<IAgentProxyResolver, AgentProxyResolver>();
        services.TryAddSingleton<MessageRouter>();
        services.TryAddSingleton<IMessageRouter>(sp => sp.GetRequiredService<MessageRouter>());

        // Expertise aggregation (#412). TryAdd so the private cloud repo can
        // decorate with tenant filters or a different cache implementation
        // without forking the OSS default. The store reads from the
        // existing agent / unit actor state keys — no new persistence.
        services.TryAddSingleton<IExpertiseStore, ActorBackedExpertiseStore>();

        // Boundary store (#413) — backed by the unit actor's own state.
        services.TryAddSingleton<IUnitBoundaryStore, ActorBackedUnitBoundaryStore>();

        // Connector persistence ports. Connector packages (GitHub, Arxiv,
        // WebSearch, …) consume these abstractions via constructor
        // injection — including from skills that both the API and the
        // Worker host register — so the defaults must live in the shared
        // Dapr module rather than in a host-specific composition root.
        // TryAdd so the private cloud repo can substitute tenant-scoped
        // implementations.
        services.TryAddSingleton<IUnitConnectorConfigStore, UnitActorConnectorConfigStore>();
        services.TryAddSingleton<IUnitConnectorRuntimeStore, UnitActorConnectorRuntimeStore>();

        // Register the base aggregator as a concrete singleton so the
        // boundary decorator can take a typed inner reference. Tests that
        // want the raw (unfiltered) aggregator can resolve the concrete
        // type directly.
        services.TryAddSingleton<ExpertiseAggregator>();

        // Boundary-filtering decorator wraps the base aggregator by default
        // (#413). Registered with TryAdd so the private cloud repo can pre-
        // register its own IExpertiseAggregator (e.g. a tenant-scoped
        // decorator) and keep it — this registration is skipped. Call sites
        // that resolve IExpertiseAggregator get the boundary-aware view for
        // free; call sites that want the raw aggregator resolve the concrete
        // ExpertiseAggregator instead.
        services.TryAddSingleton<IExpertiseAggregator>(sp =>
            new BoundaryFilteringExpertiseAggregator(
                sp.GetRequiredService<ExpertiseAggregator>(),
                sp.GetRequiredService<IUnitBoundaryStore>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILoggerFactory>()));

        // Expertise directory search (#542). Lexical / full-text default; a
        // private-cloud host or a future OSS follow-up can swap in a
        // Postgres-FTS or embedding-backed implementation by pre-registering
        // an alternative IExpertiseSearch before calling AddCvoyaSpringDapr.
        services.TryAddSingleton<IExpertiseSearch, InMemoryExpertiseSearch>();

        // Seed expertise from persisted AgentDefinition / UnitDefinition YAML
        // on actor activation (#488). TryAdd so a tenant-scoped host can swap
        // in a store-specific reader without forking. The agent/unit actors
        // depend on this via optional resolution so pre-#488 test harnesses
        // that construct actors manually keep working.
        services.TryAddSingleton<IExpertiseSeedProvider, DbExpertiseSeedProvider>();

        // Execution — AnthropicProvider needs HttpClient
        services.AddHttpClient<IAiProvider, AnthropicProvider>();
        services.AddSingleton<IPromptAssembler, PromptAssembler>();
        services.AddSingleton<IPlatformPromptProvider, PlatformPromptProvider>();

        // Dynamic model catalog (#597). Backs the unit-creation wizard's model
        // dropdown so we don't ship a stale hard-coded list for every
        // provider release. Default implementation fetches from the provider
        // models endpoint when possible (Anthropic, OpenAI, Ollama) and falls
        // back to a static curated list otherwise. TryAdd so the private cloud
        // host can swap in a tenant-scoped catalog (e.g. per-tenant API keys,
        // per-tenant allowlists) without forking the endpoint.
        services.AddHttpClient(ModelCatalog.HttpClientName);
        services.TryAddSingleton<IModelCatalog, ModelCatalog>();

        // Agent-runtime plugin registry (#678, cornerstone of the #674
        // refactor). Enumerates every DI-registered IAgentRuntime so the
        // API layer, wizard, and CLI can resolve runtimes by id without
        // importing concrete runtime packages. Per-runtime migrations
        // (#679–#682) register the concrete IAgentRuntime implementations
        // via their own AddCvoyaSpringAgentRuntime<Name>() extensions;
        // until those land, All is intentionally empty and the hardcoded
        // ModelCatalog / ProviderCredentialValidator paths above stay in
        // force. TryAdd so the private cloud host can supply a
        // tenant-scoped registry (e.g. one that filters on
        // tenant_agent_runtime_installs) without forking.
        services.TryAddSingleton<IAgentRuntimeRegistry, AgentRuntimeRegistry>();

        // Wizard-time credential validator (#655 / #660). Primary path
        // issues a lightweight GET /v1/models against the provider with
        // a caller-supplied key; for Anthropic the validator delegates
        // first to the locally-installed claude CLI (#660) so Claude.ai
        // OAuth tokens — which the Platform REST API rejects — can be
        // validated transparently. Shares the ModelCatalog HTTP client
        // pool — same handler lifecycle, low call volume. TryAdd so the
        // private cloud host can supply a tenant-scoped validator (e.g.
        // one that proxies through a tenant egress gateway) without
        // forking.
        //
        // CLI invokers are registered as enumerable singletons so
        // additional providers (codex, gemini) can plug into the same
        // seam later without touching the validator. Per #660 the
        // invoker set is intentionally small: Anthropic only. Other
        // providers still take the REST path.
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IProviderCliInvoker, ClaudeCliInvoker>());
        services.TryAddSingleton<IProviderCredentialValidator, ProviderCredentialValidator>();

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
        services.AddHttpClient(DispatcherClientContainerRuntime.HttpClientName);
        services.TryAddSingleton<IContainerRuntime, DispatcherClientContainerRuntime>();
        services.AddSingleton<IDaprSidecarManager, DaprSidecarManager>();
        services.AddSingleton<ContainerLifecycleManager>();
        services.TryAddSingleton<IUnitContainerLifecycle, UnitContainerLifecycle>();
        services.TryAddSingleton<IExecutionDispatcher, A2AExecutionDispatcher>();

        // Agent definition + tool launchers used by A2AExecutionDispatcher.
        services.TryAddSingleton<IAgentDefinitionProvider, DbAgentDefinitionProvider>();
        services.AddSingleton<IAgentToolLauncher, ClaudeCodeLauncher>();
        services.AddSingleton<IAgentToolLauncher, CodexLauncher>();
        services.AddSingleton<IAgentToolLauncher, GeminiLauncher>();
        services.AddSingleton<IAgentToolLauncher, DaprAgentLauncher>();
        services.TryAddSingleton<PersistentAgentRegistry>();
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
            services.AddHostedService(sp => sp.GetRequiredService<PersistentAgentRegistry>());
            services.AddHostedService(sp => sp.GetRequiredService<McpServer>());
        }

        // Initiative — use TryAdd so the private repo can override any implementation.
        // The Dapr state-store-backed variants are registered as defaults so policies and
        // budget state survive process restarts and are shared across replicas; the
        // in-memory implementations remain available for tests that register them first.
        services.TryAddSingleton<ICancellationManager, CancellationManager>();
        services.TryAddSingleton<IAgentPolicyStore, DaprStateAgentPolicyStore>();
        services.TryAddSingleton<IInitiativeBudgetTracker, DaprStateInitiativeBudgetTracker>();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IInitiativeEngine, InitiativeEngine>();

        // Agent-scoped initiative evaluator (#415 / PR-PLAT-INIT-1). Scoped
        // because the default implementation depends on IUnitPolicyEnforcer,
        // which in turn pulls in the scoped unit-membership repository.
        // TryAdd so the private cloud repo can layer a tenant-aware / audit
        // decorator without touching this registration.
        services.TryAddScoped<IAgentInitiativeEvaluator, DefaultAgentInitiativeEvaluator>();

        // Persistent cloning policy (#416). The repository rides the shared
        // IStateStore seam so no new component wiring is needed and the
        // rows flow through the same tenant-scoped durability story the
        // cloud host layers on every other agent-scoped setting. The
        // default enforcer is scoped because it composes the scoped
        // unit-membership repository — the private cloud repo can layer
        // a tenant-aware decorator via TryAdd without reshaping
        // persistence. The endpoint resolves IAgentCloningPolicyEnforcer
        // from the scoped request container per call.
        services.TryAddSingleton<IAgentCloningPolicyRepository, StateStoreAgentCloningPolicyRepository>();
        services.TryAddScoped<IAgentCloningPolicyEnforcer, DefaultAgentCloningPolicyEnforcer>();

        services.TryAddKeyedSingleton<ICognitionProvider, Tier1CognitionProvider>("tier1");
        services.TryAddKeyedSingleton<ICognitionProvider, Tier2CognitionProvider>("tier2");

        // Reflection-action handlers (#100). Registered via AddSingleton (not
        // TryAdd) because they are part of an enumerable registration —
        // TryAdd on IEnumerable would silently drop duplicates and the
        // registry relies on ordered iteration. The private cloud repo
        // contributes handlers through plain AddSingleton too; the
        // registry resolves duplicates "first wins" so an earlier-registered
        // cloud handler for, say, "send-message" overrides the OSS default.
        services.AddSingleton<IReflectionActionHandler, SendMessageReflectionActionHandler>();
        services.AddSingleton<IReflectionActionHandler, StartConversationReflectionActionHandler>();
        services.AddSingleton<IReflectionActionHandler, RequestHelpReflectionActionHandler>();
        services.TryAddSingleton<IReflectionActionHandlerRegistry, ReflectionActionHandlerRegistry>();

        services.AddOptions<Tier1Options>().BindConfiguration("Initiative:Tier1");
        services.AddHttpClient<Tier1CognitionProvider>();

        // Orchestration
        services.AddKeyedSingleton<IOrchestrationStrategy, AiOrchestrationStrategy>("ai");
        services.AddKeyedSingleton<IOrchestrationStrategy, WorkflowOrchestrationStrategy>("workflow");
        // Label-routed strategy (#389). Scoped keyed registration because it
        // depends on IUnitPolicyRepository (scoped) for per-turn policy reads.
        // Manifest-driven selection of this strategy per unit is tracked as
        // follow-up work; for now hosts that want label routing wire it up
        // explicitly via the keyed registration.
        services.AddKeyedScoped<IOrchestrationStrategy, LabelRoutedOrchestrationStrategy>("label-routed");

        // Unkeyed default: UnitActor (activated by the Dapr runtime via DI) takes an
        // unkeyed IOrchestrationStrategy — provide one so construction succeeds.
        // "ai" is the platform default; private-cloud code can pre-register a
        // different default via TryAdd.
        services.TryAddSingleton<IOrchestrationStrategy>(
            sp => sp.GetRequiredKeyedService<IOrchestrationStrategy>("ai"));

        // Manifest-driven orchestration strategy selection (#491). The
        // provider reads the `orchestration.strategy` key from the persisted
        // UnitDefinition JSON; the resolver combines that with `UnitPolicy.LabelRouting`
        // inference (ADR-0007) and the unkeyed default into a single
        // per-message resolution path that UnitActor consults through
        // IOrchestrationStrategyResolver.
        //
        // The inner DB provider is registered as a concrete singleton; the
        // public IOrchestrationStrategyProvider is a caching decorator (#518)
        // wrapping it. Keying off the strategy slot is hot path — every
        // domain message to a unit asks for it — but the slot only changes
        // when a manifest is re-applied. Without the decorator each message
        // opens an AsyncServiceScope and issues a FirstOrDefaultAsync against
        // Postgres; with it, steady-state traffic for a single unit
        // resolves from memory. The decorator combines a short TTL
        // (self-healing after cross-process writes) with an explicit
        // invalidation hook (immediate consistency after in-process writes).
        //
        // All three registrations are TryAdd'd so the private cloud host can
        // pre-register a tenant-scoped reader, a different cache shape, or a
        // no-op invalidator without forking. The concrete DB provider stays
        // resolvable by its concrete type for cache-off testing.
        services.TryAddSingleton<DbOrchestrationStrategyProvider>();
        services.TryAddSingleton<CachingOrchestrationStrategyProvider>(sp =>
            new CachingOrchestrationStrategyProvider(
                sp.GetRequiredService<DbOrchestrationStrategyProvider>(),
                sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILoggerFactory>()));
        services.TryAddSingleton<IOrchestrationStrategyProvider>(sp =>
            sp.GetRequiredService<CachingOrchestrationStrategyProvider>());
        services.TryAddSingleton<IOrchestrationStrategyCacheInvalidator>(sp =>
            sp.GetRequiredService<CachingOrchestrationStrategyProvider>());
        services.TryAddSingleton<IOrchestrationStrategyResolver, DefaultOrchestrationStrategyResolver>();

        // #606: read/write seam for the persisted orchestration.strategy
        // slot. Shared between UnitCreationService (manifest apply) and the
        // dedicated `/api/v1/units/{id}/orchestration` HTTP surface so the
        // two write paths cannot drift. TryAdd so the private cloud repo
        // can swap in a tenant-scoped reader before calling the OSS
        // extension.
        services.TryAddSingleton<IUnitOrchestrationStore, DbUnitOrchestrationStore>();

        // #601 / #603 / #409 B-wide: read/write seam for the persisted unit
        // execution block. Shared between UnitCreationService (manifest
        // apply) and the dedicated `/api/v1/units/{id}/execution` HTTP
        // surface so the two write paths cannot drift on shape or
        // validation. TryAdd so a hosted overlay can swap in a
        // tenant-scoped variant.
        services.TryAddSingleton<IUnitExecutionStore, DbUnitExecutionStore>();

        // #601 B-wide: companion read/write seam for the agent's own
        // execution block on AgentDefinitions.Definition. Shared between
        // manifest apply and the dedicated /api/v1/agents/{id}/execution
        // HTTP surface.
        services.TryAddSingleton<IAgentExecutionStore, DbAgentExecutionStore>();


        // Prompt
        services.AddSingleton<UnitContextBuilder>();
        services.AddSingleton<ConversationContextBuilder>();

        // State
        services.AddOptions<DaprStateStoreOptions>().BindConfiguration(DaprStateStoreOptions.SectionName);
        services.AddSingleton<IStateStore, DaprStateStore>();

        // Tenancy + Secrets. TryAdd so the private cloud repo can replace
        // any of these without touching call sites:
        //   - ITenantContext: OSS uses a singleton bound to Secrets:DefaultTenantId;
        //     private cloud swaps in a scoped resolver that reads the tenant
        //     from the authenticated principal.
        //   - ISecretStore: OSS persists plaintext via Dapr state store
        //     (dev-only; no at-rest encryption); private cloud routes writes
        //     to Azure Key Vault via the Dapr secret-store building block.
        //   - ISecretRegistry / ISecretResolver: composed from the above;
        //     decorators layer RBAC and audit logging.
        services.AddOptions<SecretsOptions>().BindConfiguration(SecretsOptions.SectionName);
        services.AddOptions<TenancyOptions>().BindConfiguration(TenancyOptions.SectionName);
        services.TryAddSingleton<ITenantContext, ConfiguredTenantContext>();
        // Cross-tenant bypass helper (#677). AsyncLocal-backed nesting-safe
        // scope with structured audit logging on open / close — the
        // EF query filters introduced in the #675 sibling PR consult its
        // IsBypassActive flag for legitimate system-wide reads
        // (DatabaseMigrator, platform analytics). TryAdd so the private
        // cloud repo can swap in a permission-checked variant (e.g. one
        // that requires a platform-admin grant on the caller principal)
        // without touching any call site.
        services.TryAddSingleton<ITenantScopeBypass, TenantScopeBypass>();
        services.TryAddSingleton<ISecretsEncryptor, SecretsEncryptor>();
        services.TryAddSingleton<ISecretStore, DaprStateBackedSecretStore>();
        services.TryAddScoped<ISecretRegistry, EfSecretRegistry>();
        services.TryAddScoped<ISecretResolver, ComposedSecretResolver>();
        // ISecretAccessPolicy: OSS default authorizes everything. The
        // private cloud repo replaces this with a tenant-admin / platform-admin
        // check driven by the authenticated principal — the endpoints only
        // depend on the interface, so no endpoint code has to change.
        services.TryAddSingleton<ISecretAccessPolicy, AllowAllSecretAccessPolicy>();

        // Observability
        services.AddSingleton<ActivityEventBus>();
        services.AddSingleton<IActivityEventBus>(sp => sp.GetRequiredService<ActivityEventBus>());
        services.AddOptions<StreamEventPublisherOptions>().BindConfiguration(StreamEventPublisherOptions.SectionName);
        services.AddSingleton<StreamEventPublisher>();
        services.AddSingleton<StreamEventSubscriber>();

        // Per-unit merged activity stream (issue #391). TryAdd so the private
        // cloud repo can decorate with tenant-scoped filtering without
        // touching the endpoint.
        services.TryAddSingleton<IUnitActivityObservable, UnitActivityObservable>();

        // Auth.
        //
        // Permission resolution (#414) is hierarchy-aware — ancestor grants
        // cascade down to descendant units by default, subject to the
        // per-unit UnitPermissionInheritance flag that plays the role of an
        // opaque boundary for the permission layer. The hierarchy resolver
        // is a DI seam so the private cloud repo can swap in a materialized
        // parent index without touching the permission service.
        services.TryAddSingleton<IUnitHierarchyResolver, DirectoryUnitHierarchyResolver>();
        services.TryAddSingleton<IPermissionService, PermissionService>();

        // Costs — scoped query/tracking services always registered for endpoint DI.
        services.AddScoped<ICostQueryService, CostAggregation>();
        services.AddScoped<ICostTracker, CloneCostTracker>();

        // Observability — query services
        services.AddScoped<IActivityQueryService, ActivityQueryService>();
        // Analytics rollups (#457). TryAdd so the private cloud repo can
        // decorate with tenant-scoped filters without forking the OSS default.
        services.TryAddScoped<IAnalyticsQueryService, AnalyticsQueryService>();

        // Conversation projection (#452 / #456). Materialises conversations
        // and inbox rows from the activity-event table — no separate message
        // store yet. TryAdd so the private cloud host can swap in a tenant-
        // scoped implementation without touching the endpoints.
        services.TryAddScoped<IConversationQueryService, ConversationQueryService>();

        // Hosted services that depend on runtime infrastructure (Dapr state store,
        // database). During build-time OpenAPI generation none of this is
        // available, so skip registration to avoid noisy startup errors. See #370.
        if (!isDocGen)
        {
            services.AddHostedService<ActivityEventPersister>();
            services.AddHostedService<CostTracker>();
            services.AddHostedService<BudgetEnforcer>();
        }

        return services;
    }

    /// <summary>
    /// Registers <see cref="DatabaseMigrator"/> as a hosted service so the
    /// containing host applies pending EF Core migrations to
    /// <see cref="SpringDbContext"/> on startup.
    /// </summary>
    /// <remarks>
    /// Call this from <strong>exactly one</strong> host in a deployment.
    /// In the OSS deployment that host is the Worker
    /// (<c>Cvoya.Spring.Host.Worker</c>); the API host
    /// (<c>Cvoya.Spring.Host.Api</c>) intentionally does not register the
    /// migrator. Registering it in multiple hosts that start
    /// concurrently against the same database races on DDL and one host
    /// will crash with <c>42P07: relation "..." already exists</c> (see
    /// issue #305).
    /// <para>
    /// The actual run is still gated by
    /// <see cref="DatabaseOptions.AutoMigrate"/>; operators that prefer
    /// to apply migrations out-of-band (CI/CD, scripted SQL) can leave
    /// the migrator registered and disable it via configuration.
    /// </para>
    /// <para>
    /// <see cref="DatabaseOptions"/> binding lives in
    /// <see cref="AddCvoyaSpringDapr"/> so non-migrating hosts still
    /// observe the configured value.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringDatabaseMigrator(this IServiceCollection services)
    {
        services.AddHostedService<DatabaseMigrator>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="DefaultTenantBootstrapService"/> as a hosted
    /// service so the containing host bootstraps the canonical
    /// <c>"default"</c> tenant on startup and invokes every registered
    /// <see cref="ITenantSeedProvider"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Mirrors the single-owner invariant of
    /// <see cref="AddCvoyaSpringDatabaseMigrator"/>: call this from
    /// <strong>exactly one</strong> host in a deployment. The OSS
    /// topology owns the bootstrap from the Worker (which already owns
    /// EF Core migrations); a private-cloud host that drives tenant
    /// provisioning out-of-band can leave it unregistered, or register
    /// it and gate the run via
    /// <see cref="TenancyOptions.BootstrapDefaultTenant"/>.
    /// </para>
    /// <para>
    /// Seed providers themselves are picked up via the standard DI
    /// graph — any <see cref="ITenantSeedProvider"/> registered before
    /// the hosted service starts participates in the run. The OSS
    /// providers register themselves inside
    /// <see cref="AddCvoyaSpringDapr"/>, so this extension is the only
    /// extra call a host needs to make.
    /// </para>
    /// <para>
    /// <see cref="TenancyOptions"/> binding lives in
    /// <see cref="AddCvoyaSpringDapr"/> so non-bootstrapping hosts can
    /// still observe the configured value.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringDefaultTenantBootstrap(this IServiceCollection services)
    {
        services.AddHostedService<DefaultTenantBootstrapService>();
        return services;
    }

    /// <summary>
    /// Registers <see cref="OllamaProvider"/> as the primary <c>IAiProvider</c> when
    /// <c>LanguageModel:Ollama:Enabled=true</c> (or when the caller forces registration
    /// via <paramref name="force"/>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hosts call this after <see cref="AddCvoyaSpringDapr"/>. It uses <c>TryAdd</c>
    /// patterns so the private cloud host can pre-register a tenant-scoped
    /// <c>IOptions&lt;OllamaOptions&gt;</c> (for per-tenant base URLs) or an alternative
    /// <c>IAiProvider</c> wrapper, and those overrides are preserved.
    /// </para>
    /// <para>
    /// Ollama exposes an OpenAI-compatible <c>/v1/chat/completions</c> endpoint and
    /// requires no API key, so a configurable base URL is the only knob. The OSS
    /// deployment defaults to the in-cluster <c>spring-ollama</c> container; macOS
    /// GPU deployments override <c>BaseUrl</c> to the host-installed Ollama.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">Configuration root used to bind <see cref="OllamaOptions"/>.</param>
    /// <param name="force">When <c>true</c>, registers the provider regardless of the
    /// <c>Enabled</c> flag — useful for test harnesses that always want the Ollama path.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringOllamaLlm(
        this IServiceCollection services,
        IConfiguration configuration,
        bool force = false)
    {
        services.AddOptions<OllamaOptions>().BindConfiguration(OllamaOptions.SectionName);

        var enabled = force || configuration.GetValue<bool>($"{OllamaOptions.SectionName}:Enabled");
        if (!enabled)
        {
            return services;
        }

        // Clear any prior IAiProvider registration. The base AddCvoyaSpringDapr call
        // registers AnthropicProvider via AddHttpClient<IAiProvider, AnthropicProvider>();
        // when Ollama is explicitly enabled we want it to win. The cloud host can
        // pre-register its own IAiProvider BEFORE this call and it will survive because
        // we only remove AnthropicProvider-shaped registrations.
        var existing = services
            .Where(d => d.ServiceType == typeof(IAiProvider) ||
                        (d.ImplementationType == typeof(AnthropicProvider)))
            .ToList();
        foreach (var descriptor in existing)
        {
            services.Remove(descriptor);
        }

        services.AddHttpClient<IAiProvider, OllamaProvider>();

        // Health-check: #616 migrated the Ollama probe into the configuration
        // validation framework. The OllamaConfigurationRequirement registers
        // as an IConfigurationRequirement; the startup validator drives the
        // probe once (matching the old OllamaHealthCheck semantics) and the
        // report surfaces the outcome consistently with every other
        // subsystem. The named HttpClient the requirement uses is registered
        // below so consumers resolving IHttpClientFactory get a configured
        // client even without AddCvoyaSpringConfigurationValidator.
        services.AddHttpClient(nameof(OllamaConfigurationRequirement));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigurationRequirement, OllamaConfigurationRequirement>());

        return services;
    }

    /// <summary>
    /// Registers the startup configuration validator (<see cref="StartupConfigurationValidator"/>)
    /// as the first <see cref="Microsoft.Extensions.Hosting.IHostedService"/>. All registered
    /// <see cref="IConfigurationRequirement"/> implementations are evaluated during
    /// <c>StartAsync</c>; a mandatory requirement reporting
    /// <see cref="ConfigurationStatus.Invalid"/> aborts host boot with its
    /// <see cref="ConfigurationRequirementStatus.FatalError"/>.
    /// </summary>
    /// <remarks>
    /// Called by <see cref="AddCvoyaSpringDapr"/> at the appropriate point
    /// in the DI graph; exposed publicly so downstream hosts that bypass
    /// <c>AddCvoyaSpringDapr</c> (for example, a narrow CLI harness) can
    /// still opt into the validator without duplicating the registration.
    /// Uses <c>TryAdd</c> so a test harness that wants to bypass startup
    /// validation can pre-register its own no-op
    /// <see cref="IStartupConfigurationValidator"/> before this runs.
    /// </remarks>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddCvoyaSpringConfigurationValidator(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Idempotent: skip on repeat calls. Keying off the concrete
        // StartupConfigurationValidator registration covers both the
        // singleton and the hosted-service wrapper.
        var alreadyRegistered = services.Any(d =>
            d.ServiceType == typeof(StartupConfigurationValidator));
        if (alreadyRegistered)
        {
            return services;
        }

        services.AddSingleton<StartupConfigurationValidator>();
        services.TryAddSingleton<IStartupConfigurationValidator>(
            sp => sp.GetRequiredService<StartupConfigurationValidator>());
        services.AddHostedService(sp => sp.GetRequiredService<StartupConfigurationValidator>());

        return services;
    }
}